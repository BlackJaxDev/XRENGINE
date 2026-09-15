using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Networking;

namespace XREngine.ControlPlane.Service;

/// <summary>Owns local worker processes; registry observations never substitute for confirmed process exit.</summary>
internal sealed class LocalWorkerSupervisor(
    LocalServiceOptions options,
    InMemoryControlPlane registry,
    LocalPackageCatalog packages,
    IWorkerProcessLauncher launcher,
    LocalHostAgentStore agentStore,
    ManagedAdmissionSigner admissionSigner,
    ILogger<LocalWorkerSupervisor> logger) : BackgroundService
{
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly ConcurrentDictionary<string, WorkerExecution> _workers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ManagedWorkerFailure> _configurationFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ManagedWorkerFailure> _staleOwnershipFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<(string AccountId, string OperationId), (string Fingerprint, string InstanceId)> _operations = [];
    private readonly HashSet<int> _reservedPorts = [];
    private bool _shuttingDown;

    /// <summary>
    /// Starts the local worker supervisor, restoring the agent store and reconciling durable workers.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous start operation.</returns>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        agentStore.RestoreRegistry(registry);
        ReconcileDurableWorkers();
        registry.RegisterHost(new ControlPlaneHostRegistration
        {
            HostId = options.HostId,
            DisplayName = "Local managed host",
            MaxInstances = options.MaxInstances,
            MaxPlayers = options.MaxPlayerSlots,
        });
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Reconciles durable worker instances by verifying their existence and launch configurations, and marks any stale or unverifiable records appropriately.
    /// </summary>
    private void ReconcileDurableWorkers()
    {
        foreach (LocalHostAgentOwnershipRecord record in agentStore.ReadLatest())
        {
            if (record.Exited || 
                string.IsNullOrWhiteSpace(record.Directory) || 
                !File.Exists(Path.Combine(record.Directory, "worker.json")))
                continue;
            
            try
            {
                ManagedWorkerLaunch? launch = JsonSerializer.Deserialize<ManagedWorkerLaunch>(File.ReadAllText(Path.Combine(record.Directory, "worker.json")), ServiceJson.Options);
                if (launch is null || launch.InstanceId != record.InstanceId || launch.Generation != record.Generation)
                {
                    MarkStale(record.InstanceId, "The durable worker record did not match its private launch configuration.");
                    continue;
                }

                // A stale or unverifiable record is still an allocation risk. Keep its endpoint unavailable
                // until an operator reconciles it, rather than accidentally reusing a live worker's port.
                _reservedPorts.Add(launch.BindPort);
                Process process;
                try { process = Process.GetProcessById(record.ProcessId); }
                catch (ArgumentException)
                {
                    registry.RecordOwnedProcessExit(launch.InstanceId, launch.Generation, record.ExitCode, new ManagedWorkerFailure
                    {
                        Code = "SessionLost",
                        Message = "The durable worker process was absent during host-agent recovery; simulation state was not restored.",
                        Retryable = true,
                    });
                    agentStore.SaveRegistry(registry);
                    continue;
                }

                if (process.HasExited || process.StartTime.ToUniversalTime() != record.ProcessStartTimeUtc)
                {
                    MarkStale(launch.InstanceId, "The durable worker process could not be verified; its endpoint remains reserved.");
                    continue;
                }

                string executable = process.MainModule?.FileName ?? string.Empty;
                string hash = string.IsNullOrEmpty(executable) ? string.Empty : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
                if (string.IsNullOrEmpty(record.ExecutableHash) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(record.ExecutableHash), Convert.FromHexString(hash)))
                {
                    MarkStale(launch.InstanceId, "The durable worker executable identity could not be verified; its endpoint remains reserved.");
                    continue;
                }

                WindowsWorkerJob? job = WindowsWorkerJob.TryOpen($"XREngine.Managed.{launch.InstanceId}.{launch.Generation:N}");
                if (job is null || !job.Contains(process))
                {
                    job?.Dispose();
                    MarkStale(launch.InstanceId, "The durable worker job membership could not be verified; its endpoint remains reserved.");
                    logger.LogWarning("Left worker {InstanceId} stale because durable job membership could not be verified.", launch.InstanceId);
                    continue;
                }

                _workers.TryAdd(launch.InstanceId, new WorkerExecution { Launch = launch, Directory = record.Directory, Process = process, Job = job, LaunchTask = Task.CompletedTask });
                logger.LogInformation("Reattached durable worker {InstanceId} generation {Generation}.", launch.InstanceId, launch.Generation);
            }
            catch (Exception exception)
            {
                MarkStale(record.InstanceId, "The durable worker ownership record could not be verified; its endpoint remains reserved.");
                logger.LogWarning(exception, "Left unverified durable worker record {InstanceId} stale; it was not adopted.", record.InstanceId);
            }
        }
    }

    /// <summary>
    /// Marks the specified worker instance as stale due to an ownership verification failure.
    /// </summary>
    /// <param name="instanceId">The ID of the worker instance to mark as stale.</param>
    /// <param name="message">The message describing the reason for marking the worker as stale.</param>
    private void MarkStale(string instanceId, string message)
        => _staleOwnershipFailures[instanceId] = new ManagedWorkerFailure { Code = "OwnershipUnverified", Message = message, Retryable = false };

    /// <summary>
    /// Creates a new local multiplayer instance based on the specified request.
    /// </summary>
    /// <param name="request">The request containing the details for the new local multiplayer instance.</param>
    /// <param name="user">The user initiating the creation of the local multiplayer instance.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A result containing the information of the created multiplayer instance, or an error if the creation failed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the creation of the local multiplayer instance fails due to an invalid operation.</exception>
    public async Task<ControlPlaneResult<MultiplayerInstanceInfo>> CreateAsync(
        CreateLocalInstanceRequest request, LocalApiUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || 
            request.OperationId.Length > 128 || 
            string.IsNullOrWhiteSpace(request.PackageId) || 
            request.PackageId.Length > 128 || 
            request.DisplayName is null || 
            request.DisplayName.Length > 128 || 
            request.MaxPlayers < 1 || 
            request.MaxPlayers > options.MaxPlayerSlots)
            return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.InvalidRequest, "Provide an operation ID, catalog package, and valid player limit.");

        await _createGate.WaitAsync(cancellationToken);

        try
        {
            if (_shuttingDown)
                return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.NoHostCapacity, "The local host is stopping.");
            
            var key = (user.UserId, request.OperationId);
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ServiceJson.Options)));
            if (_operations.TryGetValue(key, out var prior))
                return prior.Fingerprint == fingerprint ? registry.GetInstance(prior.InstanceId)
                    : ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.OperationConflict, "The operation ID was already used for a different create request.");
            
            int activeWorkers = _workers.Values.Count(worker => !worker.ExitObserved);
            if (_operations.Count >= 10_000 || (activeWorkers + 1) * options.WorkerCpuPercent > 100
                || activeWorkers + 1 > options.HostMemoryBudgetBytes / options.WorkerMemoryLimitBytes)
                return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.NoHostCapacity, "The local host resource or operation retention limit is reached.");

            WorldPackageManifest package = packages.Load(request.PackageId);
            int port = ReservePort();
            if (port == 0)
                return ControlPlaneResult<MultiplayerInstanceInfo>.Fail(ControlPlaneFailureReason.NoHostCapacity, "No available UDP endpoint remains in the configured local range.");
            
            ControlPlaneResult<MultiplayerInstanceInfo> created;
            try
            {
                created = registry.CreateManagedInstance(new CreateMultiplayerInstanceRequest
                {
                    OperationId = request.OperationId,
                    OwnerUserId = user.UserId,
                    TenantId = user.TenantId,
                    Visibility = request.IsPublic ? MultiplayerInstanceVisibility.Public : MultiplayerInstanceVisibility.Private,
                    DisplayName = request.DisplayName,
                    HostId = options.HostId,
                    Endpoint = new() { Host = options.AdvertisedHost, Port = port, ProtocolVersion = package.BuildVersion, Transport = options.RealtimeTls is null ? RealtimeTransportKind.NativeUdp : RealtimeTransportKind.NativeTls },
                    WorldAsset = package.Asset,
                    WorldPackage = package,
                    MaxPlayers = request.MaxPlayers,
                });
            }
            catch
            {
                _reservedPorts.Remove(port);
                throw;
            }
            if (!created.Success || created.Value is null)
            {
                _reservedPorts.Remove(port);
                return created;
            }

            MultiplayerInstanceInfo instance = created.Value;
            _operations[key] = (fingerprint, instance.InstanceId);
            agentStore.SaveRegistry(registry);
            WorkerExecution? execution = null;
            try
            {
                var launch = new ManagedWorkerLaunch
                {
                    OperationId = instance.OperationId,
                    InstanceId = instance.InstanceId,
                    Generation = instance.WorkerGeneration,
                    SessionId = instance.SessionId,
                    ManagementUrl = options.ListenUrl,
                    ManagementToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    BindAddress = options.BindAddress,
                    BindPort = port,
                    AdvertisedEndpoint = instance.Endpoint,
                    MaxPlayers = instance.MaxPlayers,
                    WorldPackage = package,
                    PackageRootPath = package.RootPath,
                    WorldEntryPoint = package.WorldEntryPoint,
                    GameBootstrapId = package.GameBootstrapId,
                    BuildVersion = package.BuildVersion,
                    Startup = new()
                    {
                        StartupTimeoutMilliseconds = options.StartupTimeoutSeconds * 1000,
                        ShutdownTimeoutMilliseconds = options.ShutdownTimeoutSeconds * 1000,
                        ManagementPollMilliseconds = 500,
                        ManagementLeaseMilliseconds = options.WorkerLeaseSeconds * 1000,
                    },
                    ResourceLimits = new() { CpuPercentLimit = options.WorkerCpuPercent, MemoryBytesLimit = options.WorkerMemoryLimitBytes },
                    Tls = options.RealtimeTls is null ? null : new ManagedWorkerTlsConfiguration { ListenAddress = options.RealtimeTls.ListenAddress, ListenPort = port, CertificateThumbprint = options.RealtimeTls.CertificateThumbprint, UseMachineCertificateStore = options.RealtimeTls.UseMachineCertificateStore, MaximumConnections = options.RealtimeTls.MaximumConnections, MaximumConnectionsPerAddress = options.RealtimeTls.MaximumConnectionsPerAddress },
                };
                admissionSigner.ConfigureLaunch(launch);
                execution = new WorkerExecution
                {
                    Launch = launch,
                    Directory = PrivateWorkerDirectory.Create(options.WorkingRoot, launch.Generation),
                };
                Require(registry.ConfigureManagedWorkerLaunch(launch));

                agentStore.SaveRegistry(registry);

                if (!_workers.TryAdd(instance.InstanceId, execution))
                    throw new InvalidOperationException("The worker was already allocated.");
                
                execution.LaunchTask = Task.Run(() => StageAndStart(execution), CancellationToken.None);
                return registry.GetInstance(instance.InstanceId);
            }
            catch (Exception exception)
            {
                _reservedPorts.Remove(port);
                var failure = new ManagedWorkerFailure { Code = "ConfigurationFailed", Message = "The local worker configuration could not be prepared." };
                _configurationFailures[instance.InstanceId] = failure;
                registry.RecordOwnedProcessExit(instance.InstanceId, instance.WorkerGeneration, null, failure);
                execution?.ExitObserved = true;
                logger.LogError(exception, "Unable to configure worker {InstanceId}", instance.InstanceId);
                return registry.GetInstance(instance.InstanceId);
            }
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Stages the necessary resources and starts the specified worker instance.
    /// </summary>
    /// <param name="worker">The worker instance to stage and start.</param>
    private void StageAndStart(WorkerExecution worker)
    {
        try
        {
            worker.Cancellation.Token.ThrowIfCancellationRequested();
            registry.RecordLaunchProgress(worker.Launch.InstanceId, worker.Launch.Generation, ManagedWorkerState.Staging);
            agentStore.SaveRegistry(registry);
            string staged = WorldPackageManifestBuilder.StageVerified(worker.Launch.WorldPackage,
                Path.Combine(worker.Directory, "world"), worker.Launch.PackageRootPath,
                worker.Cancellation.Token, requireAssetContentHashMatch: true);
            worker.Cancellation.Token.ThrowIfCancellationRequested();
            worker.Launch.PackageRootPath = staged;
            worker.Launch.WorldPackage.RootPath = staged;
            Require(registry.ConfigureManagedWorkerLaunch(worker.Launch));
            agentStore.SaveRegistry(registry);
            registry.RecordLaunchProgress(worker.Launch.InstanceId, worker.Launch.Generation, ManagedWorkerState.Starting);
            agentStore.SaveRegistry(registry);
            lock (worker)
            {
                worker.Cancellation.Token.ThrowIfCancellationRequested();
                launcher.Start(worker);
            }
        }
        catch (OperationCanceledException)
        {
            // An explicit stop during staging completes as stopped, never as an orphan allocation.
            if (worker.StopRequestedUtc is null)
            {
                worker.FailureCode = "LaunchCancelled";
                worker.FailureMessage = "The worker launch was cancelled.";
            }
        }
        catch (Exception exception)
        {
            worker.FailureCode = "LaunchFailed";
            worker.FailureMessage = "World staging or process launch failed; inspect the worker diagnostics.";
            RequestStop(worker.Launch.InstanceId, false);
            lock (worker)
                worker.Job?.Terminate();
                worker.Job?.Dispose();
            try { File.WriteAllText(Path.Combine(worker.Directory, "launch-error.log"), exception.ToString()); }
            catch (Exception diagnosticFailure) when (diagnosticFailure is IOException or UnauthorizedAccessException)
            { logger.LogWarning("Unable to write private launch diagnostics for {InstanceId}", worker.Launch.InstanceId); }
            logger.LogError("Launch failed for {InstanceId}; diagnostics are in its private worker directory", worker.Launch.InstanceId);
        }
    }

    /// <summary>
    /// Reports the current status of a worker instance to the control plane and receives directives in response.
    /// </summary>
    /// <param name="report">The report containing the current status of the worker instance.</param>
    /// <param name="token">The authentication token for the worker instance.</param>
    /// <returns>A result containing the directive for the worker instance, or an error if the report was invalid or authentication failed.</returns>
    public ControlPlaneResult<ManagedWorkerDirective> Report(ManagedWorkerReport report, string token)
    {
        if (!_workers.TryGetValue(report.InstanceId, out WorkerExecution? worker)
            || !SecretMatches(token, worker.Launch.ManagementToken))
            return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Worker authentication failed.");
        lock (worker)
        {
            if (worker.ExitObserved || worker.Process is null || worker.Process.HasExited || report.Generation != worker.Launch.Generation)
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.StaleWorkerGeneration, "The worker generation is no longer active.");
            if (report.ContractVersion != 1 || report.Roster.Count > worker.Launch.MaxPlayers * 2
                || report.InstalledReservationIds.Count > worker.Launch.MaxPlayers * 2
                || report.ObservedState == ManagedWorkerState.Ready && (report.TickProgress < 1 || report.BoundUdpPort != worker.Launch.BindPort))
                return ControlPlaneResult<ManagedWorkerDirective>.Fail(ControlPlaneFailureReason.InvalidRequest, "Invalid worker status or readiness evidence.");
            ControlPlaneResult<ManagedWorkerDirective> result = registry.RecordWorkerReport(report);
            if (result.Success)
            {
                foreach (ManagedAdmissionGrant grant in result.Value!.AdmissionGrants)
                    admissionSigner.Sign(grant, worker.Launch);
                worker.LastReportUtc = DateTimeOffset.UtcNow;
                worker.LastReport = report;
                agentStore.SaveRegistry(registry);
            }
            else if (result.FailureReason is ControlPlaneFailureReason.InvalidRequest or ControlPlaneFailureReason.WorldAssetMismatch)
            {
                worker.FailureCode = "WorkerContractRejected";
                worker.FailureMessage = result.Message;
                RequestStop(worker.Launch.InstanceId, false);
            }
            return result;
        }
    }

    /// <summary>
    /// Requests the specified worker instance to stop, either gracefully by draining or immediately.
    /// </summary>
    /// <param name="instanceId">The ID of the worker instance to stop.</param>
    /// <param name="drain">Indicates whether the worker should be drained gracefully before stopping.</param>
    /// <returns>True if the stop request was successfully issued; otherwise, false.</returns>
    public bool RequestStop(string instanceId, bool drain)
    {
        if (!_workers.TryGetValue(instanceId, out WorkerExecution? worker))
            return false;
        lock (worker)
        {
            if (worker.ExitObserved)
                return true;
            if (drain && worker.StopRequestedUtc is null)
                worker.DrainRequestedUtc ??= DateTimeOffset.UtcNow;
            else if (!drain)
            {
                worker.StopRequestedUtc ??= DateTimeOffset.UtcNow;
                worker.Cancellation.Cancel();
            }
            registry.SetRequestedWorkerState(instanceId, drain ? ManagedWorkerState.Draining : ManagedWorkerState.Stopping);
            agentStore.SaveRegistry(registry);
            return true;
        }
    }

    /// <summary>
    /// Retrieves the current status of the specified worker instance.
    /// </summary>
    /// <param name="instanceId">The ID of the worker instance.</param>
    /// <returns>An object representing the current status of the specified worker instance, or null if the worker does not exist.</returns>
    public object? Status(string instanceId)
    {
        if (!_workers.TryGetValue(instanceId, out WorkerExecution? worker))
            return _configurationFailures.TryGetValue(instanceId, out ManagedWorkerFailure? failure) || _staleOwnershipFailures.TryGetValue(instanceId, out failure)
                ? new { instanceId, failureCode = failure.Code, failureMessage = failure.Message, exitObserved = true }
                : null;
        lock (worker)
            return new
            {
                instanceId, worker.Launch.Generation, worker.CreatedUtc, worker.LastReportUtc,
                worker.Launch.OperationId,
                processId = worker.Process is null ? (int?)null : worker.Process.Id,
                exitCode = worker.Process?.HasExited == true ? worker.Process.ExitCode : (int?)null,
                worker.ExitObserved, worker.FailureCode, worker.FailureMessage,
                worker.LastReport?.BoundUdpPort, worker.LastReport?.TickProgress,
                loadedWorld = worker.LastReport?.LoadedWorldAsset is { } world
                    ? new { world.WorldId, world.RevisionId, world.ContentHash, world.AssetSchemaVersion, world.RequiredBuildVersion }
                    : null,
                worker.LastReport?.ObservedState, worker.LastReport?.Metrics, worker.LastReport?.Roster,
            };
    }

    /// <summary>
    /// Retrieves the launch information for the specified worker instance.
    /// </summary>
    /// <param name="instanceId">The ID of the worker instance.</param>
    /// <returns>The launch information of the specified worker instance, or null if the worker does not exist.</returns>
    public ManagedWorkerLaunch? GetLaunch(string instanceId)
        => _workers.TryGetValue(instanceId, out WorkerExecution? worker) ? worker.Launch : null;

    /// <summary>
    /// Retrieves the current resource headroom of the local worker supervisor, including available CPU and memory for new workers.
    /// </summary>
    /// <returns>An object containing the active worker count, available CPU percentage, available memory bytes, and worker resource limits.</returns>
    public object ResourceHeadroom()
    {
        int active = _workers.Values.Count(worker => !worker.ExitObserved);
        return new
        {
            activeWorkers = active, cpuPercentAvailable = Math.Max(0, 100 - active * options.WorkerCpuPercent),
            memoryBytesAvailable = Math.Max(0, options.HostMemoryBudgetBytes - active * options.WorkerMemoryLimitBytes),
            workerCpuPercent = options.WorkerCpuPercent, workerMemoryLimitBytes = options.WorkerMemoryLimitBytes,
        };
    }

    /// <summary>
    /// Executes the main loop of the local worker supervisor, periodically observing the state of all workers and performing necessary actions.
    /// </summary>
    /// <param name="stoppingToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous execution of the supervisor loop.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            registry.HeartbeatHost(options.HostId);
            foreach (WorkerExecution worker in _workers.Values)
                await ObserveAsync(worker);
            try { await Task.Delay(250, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// Observes the state of the specified worker and takes appropriate actions based on its status.
    /// </summary>
    /// <param name="worker">The worker to observe.</param>
    /// <returns>A task that represents the asynchronous observation operation.</returns>
    private async Task ObserveAsync(WorkerExecution worker)
    {
        if (worker.ExitObserved)
            return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (worker)
        {
            bool ready = worker.LastReport?.ObservedState is ManagedWorkerState.Ready or ManagedWorkerState.Draining;
            if (worker.StopRequestedUtc is null && !ready && (now - worker.CreatedUtc).TotalSeconds > options.StartupTimeoutSeconds)
            {
                worker.FailureCode = "StartupTimeout";
                worker.FailureMessage = "The worker did not become ready before its startup deadline.";
                RequestStop(worker.Launch.InstanceId, false);
            }
            if (worker.StopRequestedUtc is null && worker.LastReportUtc is { } reported
                && (now - reported).TotalSeconds > options.WorkerLeaseSeconds)
            {
                worker.FailureCode = "WorkerLeaseExpired";
                worker.FailureMessage = "The worker stopped reporting liveness.";
                RequestStop(worker.Launch.InstanceId, false);
            }
            if (worker.StopRequestedUtc is null && worker.DrainRequestedUtc is { } drain
                && (worker.LastReport?.Roster.Count == 0 || (now - drain).TotalSeconds > options.DrainTimeoutSeconds))
                RequestStop(worker.Launch.InstanceId, false);
            if (worker.Process is not null && !worker.Process.HasExited
                && worker.StopRequestedUtc is { } stop && (now - stop).TotalSeconds > options.ShutdownTimeoutSeconds)
            {
                worker.FailureCode ??= "ShutdownTimeout";
                worker.FailureMessage ??= "The worker exceeded its shutdown deadline and was terminated.";
                worker.Job?.Terminate();
                worker.Job?.Dispose();
            }
        }
        if (worker.LaunchTask is null || !worker.LaunchTask.IsCompleted || worker.Process is not null && !worker.Process.HasExited)
            return;

        await _createGate.WaitAsync();
        try
        {
            lock (worker)
            {
                if (worker.ExitObserved)
                    return;
                int? exitCode = worker.Process?.ExitCode;
                ManagedWorkerFailure? failure = worker.FailureCode is not null ? new()
                {
                    Code = worker.FailureCode, Message = worker.FailureMessage ?? "Worker failed.", Retryable = false,
                } : worker.StopRequestedUtc is null || exitCode is not null and not 0 ? new()
                {
                    Code = "UnexpectedExit", Message = "The worker exited without a stop request.", Retryable = true,
                } : null;
                registry.RecordOwnedProcessExit(worker.Launch.InstanceId, worker.Launch.Generation,
                    exitCode, failure);
                worker.FailureCode = failure?.Code;
                worker.FailureMessage = failure?.Message;
                worker.ExitObserved = true;
                agentStore.RecordExited(worker, exitCode);
                agentStore.SaveRegistry(registry);
                worker.Job?.Terminate();
                worker.Job?.Dispose();
                _reservedPorts.Remove(worker.Launch.BindPort);
                logger.LogInformation("Worker {InstanceId} exited: {ExitCode}; {Failure}", worker.Launch.InstanceId, exitCode, failure?.Code);
            }
        }
        finally { _createGate.Release(); }
    }

    /// <summary>
    /// Stops the supervisor and requests all owned workers to stop gracefully.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous stop operation.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _createGate.WaitAsync(CancellationToken.None);
        try { _shuttingDown = true; }
        finally { _createGate.Release(); }
        foreach (string id in _workers.Keys)
            RequestStop(id, false);
        try { await base.StopAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await ConvergeOwnedWorkersAsync(); }
    }

    /// <summary>
    /// Ensures that all owned workers are observed and properly cleaned up during shutdown.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ConvergeOwnedWorkersAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(options.ShutdownTimeoutSeconds);
        try
        {
            while (_workers.Values.Any(worker => !worker.ExitObserved) && DateTimeOffset.UtcNow < deadline)
            {
                foreach (WorkerExecution worker in _workers.Values)
                    await ObserveAsync(worker);
                await Task.Delay(100, CancellationToken.None);
            }
        }
        finally
        {
            // Close every owned job before awaiting any one worker. A failed task cannot skip another job.
            foreach (WorkerExecution worker in _workers.Values)
                lock (worker)
                    worker.Job?.Dispose();
            foreach (WorkerExecution worker in _workers.Values)
            {
                try
                {
                    if (worker.LaunchTask is not null)
                        await worker.LaunchTask.WaitAsync(TimeSpan.FromSeconds(5));
                    if (worker.Process is not null)
                        await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await ObserveAsync(worker);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Cleanup observation failed for owned worker {InstanceId}", worker.Launch.InstanceId);
                }
            }
        }
    }

    /// <summary>
    /// Reserves an available UDP port within the configured range.
    /// </summary>
    /// <returns>The reserved UDP port number, or 0 if no port could be reserved.</returns>
    private int ReservePort()
    {
        for (int port = options.FirstUdpPort; port <= options.LastUdpPort; port++)
        {
            if (_reservedPorts.Contains(port))
                continue;
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            try { probe.Bind(new IPEndPoint(IPAddress.Parse(options.BindAddress), port)); }
            catch (SocketException) { continue; }
            _reservedPorts.Add(port);
            return port;
        }
        return 0;
    }

    /// <summary>
    /// Determines whether the supplied secret matches the expected secret.
    /// </summary>
    /// <param name="supplied">The supplied secret to check.</param>
    /// <param name="expected">The expected secret to match against.</param>
    /// <returns>True if the supplied secret matches the expected secret; otherwise, false.</returns>
    private static bool SecretMatches(string supplied, string expected)
        => supplied.Length is > 0 and <= 256 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>
    /// Throws an InvalidOperationException if the result indicates failure.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to check for success.</param>
    /// <exception cref="InvalidOperationException"></exception>
    private static void Require<T>(ControlPlaneResult<T> result)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
    }
}
