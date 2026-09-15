using System.Numerics;
using System.Diagnostics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Scene.Transforms;

namespace XREngine;

public partial class ServerNetworkingManager
{
    private const float CharacterLocomotionSpeed = 5.0f;
    // A bounded rolling sample is sufficient for host health reports and keeps the fixed path allocation-free.
    private const int SimulationMetricWindowTicks = 256;
    private readonly List<(PlayerTransformUpdate Update, NetworkPlayerConnection Owner)> _simulatedTransforms = [];
    private long _simulatedInputCount;
    private long _rejectedInputCount;
    private long _authoritativeTransformBytes;
    private int _lastInputQueueDepth;
    private double _lastOldestInputAgeMilliseconds;
    private double _lastSimulationTickMilliseconds;
    private double _configuredFixedTickMilliseconds;
    private double _lastTickBudgetHeadroomMilliseconds;
    private long _lastSimulationTickAllocatedBytes;
    private long _totalSimulationTickCount;
    private int _sampleWindowTickCount;
    private double _sampleWindowTotalTickMilliseconds;
    private double _sampleWindowPeakTickMilliseconds;
    private long _sampleWindowAllocatedBytes;

    public AuthoritativeSimulationMetrics GetAuthoritativeSimulationMetrics()
        => new(_replication.CurrentServerTickId, _configuredFixedTickMilliseconds, _lastSimulationTickMilliseconds, _lastTickBudgetHeadroomMilliseconds,
            Interlocked.Read(ref _lastSimulationTickAllocatedBytes), Interlocked.Read(ref _totalSimulationTickCount), _sampleWindowTickCount,
            _sampleWindowTickCount == 0 ? 0.0d : _sampleWindowTotalTickMilliseconds / _sampleWindowTickCount, _sampleWindowPeakTickMilliseconds,
            Interlocked.Read(ref _sampleWindowAllocatedBytes), _lastInputQueueDepth, _lastOldestInputAgeMilliseconds,
            Interlocked.Read(ref _simulatedInputCount), Interlocked.Read(ref _rejectedInputCount), Interlocked.Read(ref _authoritativeTransformBytes));

    /// <summary>
    /// Runs at the engine fixed cadence. UDP receipt only buffers input; this
    /// method is the sole path that advances a processed-input acknowledgement.
    /// </summary>
    private void AdvanceSimulationTick()
    {
        long startTicks = Stopwatch.GetTimestamp();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        _replication.AdvanceServerTick();
        double nowUtc = GetUtcSeconds();
        float fixedDelta = Math.Clamp(_simulationTiming?.FixedDeltaSeconds ?? (1.0f / 60.0f), 0.001f, 0.1f);
        _configuredFixedTickMilliseconds = fixedDelta * 1000.0d;
        _lastInputQueueDepth = 0;

        lock (_playerLock)
        {
            _simulatedTransforms.Clear();
            foreach (NetworkPlayerConnection connection in _playersByIndex.Values)
            {
                if (!_replication.TryConsumeInput(connection.ServerPlayerIndex, connection.LastProcessedInputSequence, nowUtc, out PlayerInputSnapshot? snapshot, out int depth)
                    || snapshot is null)
                {
                    connection.InputBufferDepth = depth;
                    continue;
                }

                connection.InputBufferDepth = depth;
                _lastInputQueueDepth += depth;
                if (!ValidateAuthority(connection, snapshot.EntityId, nowUtc, out _))
                {
                    Interlocked.Increment(ref _rejectedInputCount);
                    continue;
                }

                PlayerTransformUpdate? update = null;
                bool applied = snapshot.Input switch
                {
                    CharacterPawnInputSnapshot input => TrySimulateCharacterLocomotion(connection, input, snapshot, fixedDelta, nowUtc, out update),
                    GameInputSnapshot gameInput => GameInputSchemaRegistry.TryApply(gameInput, snapshot),
                    _ => false,
                };
                if (!applied)
                {
                    Interlocked.Increment(ref _rejectedInputCount);
                    continue;
                }

                if (_replication.TryRenewLease(
                    connection.NetworkEntityId,
                    connection.SessionId,
                    connection.ClientId,
                    connection.ServerPlayerIndex,
                    nowUtc,
                    out NetworkAuthorityLease? renewedLease))
                {
                    connection.AuthorityLease = renewedLease;
                }
                connection.LastProcessedInputSequence = snapshot.InputSequence;
                connection.LastInput = snapshot;
                if (update is not null)
                {
                    connection.LastTransform = update;
                    connection.RelevanceCenter = update.Translation;
                }
                connection.LastHeardUtc = DateTime.UtcNow;
                if (update is not null)
                    _simulatedTransforms.Add((update, connection));
                Interlocked.Increment(ref _simulatedInputCount);
            }
        }

        foreach ((PlayerTransformUpdate update, NetworkPlayerConnection owner) in _simulatedTransforms)
        {
            if (owner.Pawn?.Controller is IPawnController controller)
                controller.ApplyNetworkTransform(update);
            SendAuthoritativeTransformUpdate(update, owner, nowUtc);
        }
        _lastOldestInputAgeMilliseconds = _replication.GetOldestBufferedInputAgeSeconds(nowUtc) * 1000.0d;
        _lastSimulationTickMilliseconds = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        _lastTickBudgetHeadroomMilliseconds = _configuredFixedTickMilliseconds - _lastSimulationTickMilliseconds;
        _lastSimulationTickAllocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        _totalSimulationTickCount++;
        _sampleWindowTickCount++;
        _sampleWindowTotalTickMilliseconds += _lastSimulationTickMilliseconds;
        _sampleWindowPeakTickMilliseconds = Math.Max(_sampleWindowPeakTickMilliseconds, _lastSimulationTickMilliseconds);
        _sampleWindowAllocatedBytes += _lastSimulationTickAllocatedBytes;
        if (_sampleWindowTickCount >= SimulationMetricWindowTicks)
        {
            _sampleWindowTickCount = 0;
            _sampleWindowTotalTickMilliseconds = 0.0d;
            _sampleWindowPeakTickMilliseconds = 0.0d;
            _sampleWindowAllocatedBytes = 0L;
        }
    }

    private bool TrySimulateCharacterLocomotion(
        NetworkPlayerConnection connection,
        CharacterPawnInputSnapshot input,
        PlayerInputSnapshot snapshot,
        float fixedDelta,
        double nowUtc,
        out PlayerTransformUpdate update)
    {
        update = null!;
        if (!IsFinite(input.Movement) || !IsFinite(input.ViewAngles)
            || connection.Pawn is not { } pawn
            || pawn.SceneNode?.Transform is not Transform transform
            || connection.TransformId == Guid.Empty)
        {
            return false;
        }

        Vector3 velocity;
        if (pawn is IAuthoritativeCharacterInputSimulator simulator)
        {
            if (!simulator.TrySimulateAuthoritativeInput(pawn, input, fixedDelta, out velocity) || !IsFinite(velocity))
                return false;
        }
        else
        {
            if (!EnableKinematicCharacterLocomotion)
                return false;

            Vector2 movement = input.Movement;
            if (movement.LengthSquared() > 1.0f)
                movement = Vector2.Normalize(movement);

            velocity = new Vector3(movement.X * CharacterLocomotionSpeed, 0.0f, movement.Y * CharacterLocomotionSpeed);
            transform.Translation += velocity * fixedDelta;
        }

        update = _replication.StampAuthoritativeTransform(new PlayerTransformUpdate
        {
            ServerPlayerIndex = connection.ServerPlayerIndex,
            EntityId = connection.NetworkEntityId,
            TransformId = connection.TransformId,
            Translation = transform.Translation,
            Rotation = transform.Rotation,
            Velocity = velocity,
            SessionId = connection.SessionId,
            ClientInputSequence = snapshot.InputSequence,
            LastProcessedInputSequence = snapshot.InputSequence,
            AuthorityMode = NetworkAuthorityMode.ServerAuthoritative,
            IsServerCorrection = true,
        }, nowUtc);
        return true;
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

}
