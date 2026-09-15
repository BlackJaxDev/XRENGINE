using System.Numerics;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Scene.Transforms;
using XREngine.Timers;

namespace XREngine;

public partial class ClientNetworkingManager
{
    private const float PredictedCharacterLocomotionSpeed = 5.0f;
    private readonly object _predictionLock = new();
    private readonly Dictionary<int, SortedDictionary<uint, PlayerInputSnapshot>> _predictedInputs = [];
    private long _correctionCount;
    private double _lastCorrectionMagnitudeMeters;
    private double _peakCorrectionMagnitudeMeters;

    /// <summary>Returns measured prediction drift before authoritative corrections are applied.</summary>
    public ClientPredictionMetrics GetPredictionMetrics()
        => new(Interlocked.Read(ref _correctionCount), _lastCorrectionMagnitudeMeters, _peakCorrectionMagnitudeMeters);

    private void RecordPredictionCorrection(Vector3 predicted, Vector3 authoritative)
    {
        double magnitude = Vector3.Distance(predicted, authoritative);
        _lastCorrectionMagnitudeMeters = magnitude;
        _peakCorrectionMagnitudeMeters = Math.Max(_peakCorrectionMagnitudeMeters, magnitude);
        Interlocked.Increment(ref _correctionCount);
    }

    /// <summary>
    /// Game code can submit the supported locomotion schema without coupling to a
    /// particular pawn implementation. Unsupported interaction schemas remain
    /// outside this realtime authority channel until explicitly registered.
    /// </summary>
    public bool SubmitPredictedCharacterInput(CharacterPawnInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsGameplayReady || !HasValidLocalAssignment || !IsFinite(input.Movement) || !IsFinite(input.ViewAngles))
            return false;

        PlayerInputSnapshot snapshot = new()
        {
            ServerPlayerIndex = _primaryAssignedServerPlayerIndex,
            EntityId = _primaryAssignedEntityId,
            Input = input,
            TimestampUtc = GetUtcSeconds(),
            ClientSendTimestampUtc = GetUtcSeconds(),
            InputSequence = ++_inputSequence,
            ClientTickId = _clientTickId,
            SessionId = _activeSessionId,
        };
        RecordPredictedInput(snapshot);
        BroadcastStateChange(EStateChangeType.PlayerInputSnapshot, snapshot, compress: true);
        return true;
    }

    public bool SubmitGameInput(GameInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsGameplayReady || !HasValidLocalAssignment || !GameInputSchemaRegistry.TryValidate(input, out _))
            return false;
        PlayerInputSnapshot snapshot = new()
        {
            ServerPlayerIndex = _primaryAssignedServerPlayerIndex,
            EntityId = _primaryAssignedEntityId,
            Input = input,
            TimestampUtc = GetUtcSeconds(), ClientSendTimestampUtc = GetUtcSeconds(),
            InputSequence = ++_inputSequence, ClientTickId = _clientTickId, SessionId = _activeSessionId,
        };
        RecordPredictedInput(snapshot);
        BroadcastStateChange(EStateChangeType.PlayerInputSnapshot, snapshot, compress: true);
        return true;
    }

    /// <summary>Keeps only replayable, AOT-known input commands until their server acknowledgement arrives.</summary>
    private void RecordPredictedInput(PlayerInputSnapshot snapshot)
    {
        if (!IsManagedTransportRequested || snapshot.Input is not CharacterPawnInputSnapshot || snapshot.InputSequence == 0)
            return;

        lock (_predictionLock)
        {
            if (!_predictedInputs.TryGetValue(snapshot.ServerPlayerIndex, out SortedDictionary<uint, PlayerInputSnapshot>? history))
            {
                history = [];
                _predictedInputs[snapshot.ServerPlayerIndex] = history;
            }

            history[snapshot.InputSequence] = snapshot;
            while (history.Count > MultiplayerRuntimePolicy.MaxBufferedInputsPerPlayer)
                history.Remove(history.First().Key);
        }
    }

    /// <summary>Rewinds to an authoritative correction and reapplies only unacknowledged locomotion input.</summary>
    private void ReplayPredictedInputs(IPawnController player, PlayerTransformUpdate correction)
    {
        if (!IsManagedTransportRequested
            || player.ControlledPawnComponent?.SceneNode?.Transform is not Transform transform)
        {
            return;
        }

        lock (_predictionLock)
        {
            if (!_predictedInputs.TryGetValue(correction.ServerPlayerIndex, out SortedDictionary<uint, PlayerInputSnapshot>? history))
                return;

            while (history.Count > 0 && history.First().Key <= correction.LastProcessedInputSequence)
                history.Remove(history.First().Key);

            float fixedDelta = Math.Clamp(RuntimeTimingServices.Current.FixedDeltaSeconds, 0.001f, 0.1f);
            foreach (PlayerInputSnapshot snapshot in history.Values)
            {
                if (snapshot.Input is not CharacterPawnInputSnapshot input || !IsFinite(input.Movement))
                    continue;

                Vector2 movement = input.Movement;
                if (movement.LengthSquared() > 1.0f)
                    movement = Vector2.Normalize(movement);
                transform.Translation += new Vector3(movement.X, 0.0f, movement.Y) * (PredictedCharacterLocomotionSpeed * fixedDelta);
            }
        }
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private void ClearPredictedInputs()
    {
        lock (_predictionLock)
            _predictedInputs.Clear();
    }
}
