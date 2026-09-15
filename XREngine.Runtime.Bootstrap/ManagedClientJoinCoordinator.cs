using XREngine.ControlPlane;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Coordinates verified managed launches while serializing all world and networking mutation at simulation boundaries.</summary>
public sealed class ManagedClientJoinCoordinator : IDisposable
{
    private readonly object _sync = new();
    private ManagedClientJoinStatus _status = new() { State = ManagedClientJoinState.Idle };
    private CancellationTokenSource? _operationCancellation;
    private ManagedClientLaunchLease? _contentLease;
    private ManagedInstanceServiceClient? _reservationClient;
    private string? _instanceId;
    private string? _reservationId;
    private long _operationGeneration;
    private bool _disposed;

    public ManagedClientJoinStatus Status { get { lock (_sync) return _status; } }
    public event Action<ManagedClientJoinStatus>? StatusChanged;

    /// <summary>Downloads a delivered launch and applies it only after the simulation boundary accepts this operation.</summary>
    public async Task<XRWorld> JoinAsync(ManagedInstanceServiceClient service, string instanceId, string reservationId,
        RemoteWorldPackageCache packageCache, CancellationToken cancellationToken = default, IProgress<WorldPackageStagingProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        Publish(generation, ManagedClientJoinState.Loading, "Downloading and verifying managed world content.");
        ManagedClientLaunchLease? lease = null;
        ManagedInstanceServiceClient? sessionClient = null;
        try
        {
            lease = await service.AcquireLaunchAsync(instanceId, reservationId, packageCache, token, progress).ConfigureAwait(false);
            sessionClient = service.CreateSessionClient();
            XRWorld world = await ApplyAtSimulationBoundaryAsync(generation, lease, sessionClient, instanceId, reservationId, token, progress).ConfigureAwait(false);
            lease = null;
            sessionClient = null;
            return world;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(generation, ManagedClientJoinState.Idle, "Managed join was cancelled.", failureKind: ManagedClientJoinFailureKind.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            Publish(generation, ManagedClientJoinState.Failed, "Managed join failed before readiness was observed.", exception, ClassifyStartupFailure(exception));
            throw;
        }
        finally
        {
            lease?.Dispose();
            sessionClient?.Dispose();
        }
    }

    /// <summary>Applies an already acquired launch. This overload has no retained service credential, so callers own explicit reservation revocation.</summary>
    public async Task<XRWorld> JoinAsync(ManagedClientLaunchLease lease, CancellationToken cancellationToken = default,
        IProgress<WorldPackageStagingProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        Publish(generation, ManagedClientJoinState.Loading, "Staging and verifying managed world content.");
        try
        {
            return await ApplyAtSimulationBoundaryAsync(generation, lease, null, null, null, token, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lease.Dispose();
            Publish(generation, ManagedClientJoinState.Idle, "Managed join was cancelled.", failureKind: ManagedClientJoinFailureKind.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            lease.Dispose();
            Publish(generation, ManagedClientJoinState.Failed, "Managed join failed before readiness was observed.", exception, ClassifyStartupFailure(exception));
            throw;
        }
    }

    /// <summary>Projects transport, assignment, and replication state into a credential-free lifecycle status.</summary>
    public void Observe()
    {
        if (Engine.Networking is not ClientNetworkingManager client)
            return;
        if (!string.IsNullOrWhiteSpace(client.ManagedTransportFailure) || !string.IsNullOrWhiteSpace(client.EncryptedTransportFailure))
        {
            PublishCurrent(ManagedClientJoinState.Failed, "Managed realtime transport became unavailable.", ManagedClientJoinFailureKind.EndpointUnreachable);
            return;
        }
        if (client.ReplicationSynchronizationState == ClientReplicationSynchronizationState.Failed)
        {
            PublishCurrent(ManagedClientJoinState.Failed, "Managed synchronization was interrupted.", ManagedClientJoinFailureKind.SynchronizationInterrupted);
            return;
        }
        if (client.LastServerError is { } error)
        {
            PublishCurrent(ManagedClientJoinState.Failed, DescribeServerError(error), ClassifyServerError(error));
            return;
        }
        if (client.IsGameplayReady)
            PublishCurrent(ManagedClientJoinState.Ready, "Managed assignment and synchronization are complete.");
        else if (client.HasValidLocalAssignment)
            PublishCurrent(ManagedClientJoinState.Synchronizing, "Managed assignment received; synchronizing world state.");
    }

    /// <summary>Revokes the active reservation first, then closes the local transport and releases its content lease.</summary>
    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        Publish(generation, ManagedClientJoinState.Leaving, "Leaving managed instance.");
        ManagedInstanceServiceClient? client;
        string? instanceId;
        string? reservationId;
        lock (_sync)
        {
            client = _reservationClient;
            instanceId = _instanceId;
            reservationId = _reservationId;
            _reservationClient = null;
            _instanceId = null;
            _reservationId = null;
        }
        Exception? revokeFailure = null;
        try
        {
            if (client is not null && instanceId is not null && reservationId is not null)
                await client.DeleteReservationAsync(instanceId, reservationId, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            revokeFailure = exception;
        }
        finally
        {
            client?.Dispose();
            await CleanupAtSimulationBoundaryAsync(generation).ConfigureAwait(false);
        }
        if (revokeFailure is not null)
            Publish(generation, ManagedClientJoinState.Failed, "Managed transport closed, but reservation revocation was not confirmed.", revokeFailure, ManagedClientJoinFailureKind.EndpointUnreachable);
        else
            Publish(generation, ManagedClientJoinState.Idle, "Managed instance left.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _operationCancellation?.Cancel();
            _reservationClient?.Dispose();
            _reservationClient = null;
            Interlocked.Exchange(ref _contentLease, null)?.Dispose();
        }
    }

    private async Task<XRWorld> ApplyAtSimulationBoundaryAsync(long generation, ManagedClientLaunchLease lease,
        ManagedInstanceServiceClient? sessionClient, string? instanceId, string? reservationId, CancellationToken token,
        IProgress<WorldPackageStagingProgress>? progress)
    {
        var completion = new TaskCompletionSource<XRWorld>(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueSimulationBoundaryTask(() =>
        {
            try
            {
                ThrowIfInactive(generation, token);
                GameStartupSettings settings = Engine.GameSettings.DeepClone();
                XRWorld world = ManagedClientWorldLoader.Load(lease.Launch, settings, token, progress);
                settings.IgnoreEnvironmentRealtimeHandoffs = true;
                ThrowIfInactive(generation, token);
                Engine.ConfigureNetworking(new GameStartupSettings { NetworkingType = ENetworkingType.Local, IgnoreEnvironmentRealtimeHandoffs = true });
                RetargetPrimaryWorld(world);
                ThrowIfInactive(generation, token);
                Engine.ConfigureNetworking(settings);
                CommitSession(generation, lease, sessionClient, instanceId, reservationId);
                Publish(generation, ManagedClientJoinState.Connecting, "Opening the managed realtime transport.");
                completion.TrySetResult(world);
            }
            catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task CleanupAtSimulationBoundaryAsync(long generation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueSimulationBoundaryTask(() =>
        {
            try
            {
                if (IsCurrent(generation))
                {
                    Engine.ConfigureNetworking(new GameStartupSettings { NetworkingType = ENetworkingType.Local, IgnoreEnvironmentRealtimeHandoffs = true });
                    Interlocked.Exchange(ref _contentLease, null)?.Dispose();
                }
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        await completion.Task.ConfigureAwait(false);
    }

    private long BeginOperation(CancellationToken cancellationToken, out CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ManagedClientJoinCoordinator));
            _operationCancellation?.Cancel();
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _operationCancellation.Token;
            return ++_operationGeneration;
        }
    }

    private void CommitSession(long generation, ManagedClientLaunchLease lease, ManagedInstanceServiceClient? client, string? instanceId, string? reservationId)
    {
        ThrowIfInactive(generation, CancellationToken.None);
        lock (_sync)
        {
            if (!IsCurrent_NoLock(generation))
                throw new OperationCanceledException();
            Interlocked.Exchange(ref _contentLease, lease)?.Dispose();
            _reservationClient?.Dispose();
            _reservationClient = client;
            _instanceId = instanceId;
            _reservationId = reservationId;
        }
    }

    private void ThrowIfInactive(long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(generation))
            throw new OperationCanceledException(token);
    }

    private bool IsCurrent(long generation) { lock (_sync) return IsCurrent_NoLock(generation); }
    private bool IsCurrent_NoLock(long generation) => !_disposed && _operationGeneration == generation;

    private static void RetargetPrimaryWorld(XRWorld targetWorld)
    {
        IRuntimeWorldHostServices host = RuntimeWorldHostServices.Current ?? throw new InvalidOperationException("Managed instance switching requires an installed runtime world host.");
        RuntimeWorld? current = Engine.WorldInstances.FirstOrDefault();
        if (current is null) _ = host.GetOrCreate(targetWorld);
        else host.Retarget(current, targetWorld);
    }

    private static ManagedClientJoinFailureKind ClassifyStartupFailure(Exception exception)
        => exception.Message.Contains("world", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("build", StringComparison.OrdinalIgnoreCase)
            ? ManagedClientJoinFailureKind.WorldOrBuildMismatch
            : exception is OperationCanceledException ? ManagedClientJoinFailureKind.Cancelled : ManagedClientJoinFailureKind.Unknown;

    private static ManagedClientJoinFailureKind ClassifyServerError(ServerErrorMessage error)
    {
        if ((error.Title + " " + error.Detail).Contains("kick", StringComparison.OrdinalIgnoreCase)) return ManagedClientJoinFailureKind.Kicked;
        return error.StatusCode switch { 400 => ManagedClientJoinFailureKind.WorldOrBuildMismatch, 401 or 403 => ManagedClientJoinFailureKind.InvalidOrExpiredTicket, 404 or 425 => ManagedClientJoinFailureKind.ServerUnready, 409 => ManagedClientJoinFailureKind.RoomFull, >= 500 => ManagedClientJoinFailureKind.ServerExited, _ => ManagedClientJoinFailureKind.Unknown };
    }
    private static string DescribeServerError(ServerErrorMessage error) => string.IsNullOrWhiteSpace(error.Title) ? "Managed server rejected the join." : error.Title;

    private void PublishCurrent(ManagedClientJoinState state, string message, ManagedClientJoinFailureKind kind = ManagedClientJoinFailureKind.None)
    {
        long generation; lock (_sync) generation = _operationGeneration;
        Publish(generation, state, message, failureKind: kind);
    }
    private void Publish(long generation, ManagedClientJoinState state, string message, Exception? failure = null, ManagedClientJoinFailureKind failureKind = ManagedClientJoinFailureKind.None)
    {
        ManagedClientJoinStatus next = new() { State = state, Message = message, Failure = failure, FailureKind = failureKind };
        bool changed;
        lock (_sync)
        {
            if (!IsCurrent_NoLock(generation)) return;
            changed = _status.State != next.State || !string.Equals(_status.Message, next.Message, StringComparison.Ordinal);
            _status = next;
        }
        if (changed) StatusChanged?.Invoke(next);
    }
}
