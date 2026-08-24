using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanPipelineManager
{
    private const string VulkanPipelineCompileWorkersEnvVar = XREngineEnvironmentVariables.VulkanPipelineCompileWorkers;
    private const double VulkanPipelineCompileWarningSeconds = 2.0;
    private const double VulkanPipelineCompileQuarantineSeconds = 10.0;


    internal bool IsAsyncCompilationEnabled(
        bool deviceReady,
        bool acceptsBackendWork,
        bool asyncCompilationEnabled)
        => asyncCompilationEnabled &&
           deviceReady &&
           acceptsBackendWork &&
           Volatile.Read(ref _vulkanPipelineCompileShutdownStarted) == 0;

    internal ulong CompileActivityGeneration
        => unchecked((ulong)Volatile.Read(ref _vulkanPipelineCompileActivityGeneration));

    internal long CompileDependencyGeneration
        => Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration);

    internal bool TryTakeCompletedGraphicsPipeline(
        in VulkanGraphicsPipelineCompileKey key,
        out VulkanGraphicsPipelineCompileResult result)
    {
        DrainSupersededSharedGraphicsPipelines();
        InspectPipelineCompileHealth();
        result = default;
        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            if (!_vulkanGraphicsPipelineCompileJobs.TryGetValue(key, out VulkanGraphicsPipelineCompileJob? job) ||
                !job.Task.IsCompleted)
            {
                return false;
            }

            result = job.Task.GetAwaiter().GetResult();
            if (result is { Success: true, Pipeline.Handle: not 0 })
            {
                Pipeline published = StoreOrRetireSharedGraphicsPipeline(
                    job.Request.Key,
                    result.Pipeline);
                result = result with { Pipeline = published };
            }

            if (!_vulkanGraphicsPipelineCompileJobs.TryRemove(key, out job))
                return false;

            ReleaseProgramCompileReservation(job.Request);
            Interlocked.Increment(ref _vulkanPipelineCompileActivityGeneration);
            return true;
        }
    }

    internal bool IsGraphicsPipelineCompileInFlight(in VulkanGraphicsPipelineCompileKey key)
    {
        DrainSupersededSharedGraphicsPipelines();
        InspectPipelineCompileHealth();
        return _vulkanGraphicsPipelineCompileJobs.ContainsKey(key);
    }

    internal bool TryEnqueueGraphicsPipelineCompile(
        VulkanGraphicsPipelineBuildRequest request,
        bool acceptsBackendWork,
        bool asyncCompilationEnabled,
        out string rejectReason)
    {
        DrainSupersededSharedGraphicsPipelines();
        rejectReason = string.Empty;
        InspectPipelineCompileHealth();
        if (!IsAsyncCompilationEnabled(
                RequireDeviceContext().IsReady,
                acceptsBackendWork,
                asyncCompilationEnabled))
        {
            rejectReason = "async Vulkan pipeline compilation is disabled";
            return false;
        }

        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            if (Volatile.Read(ref _vulkanPipelineCompileShutdownStarted) != 0 ||
                !acceptsBackendWork)
            {
                rejectReason = "renderer backend retirement has begun";
                return false;
            }

            if (Volatile.Read(ref _vulkanPipelineCompileDependencyMutationActive) != 0)
            {
                rejectReason = "shader or pipeline-layout dependencies are being replaced";
                return false;
            }

            if (request.DependencyGeneration !=
                Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration))
            {
                rejectReason = "pipeline build request captured a retired shader or pipeline-layout generation";
                return false;
            }

            if (_vulkanGraphicsPipelineCompileJobs.ContainsKey(request.CompileKey))
            {
                rejectReason = "pipeline compile job is already queued";
                return false;
            }

            if (_vulkanGraphicsPipelineProgramCompileJobs.ContainsKey(request.Key.ProgramPipelineHash))
            {
                rejectReason =
                    $"another cold pipeline for program 0x{request.Key.ProgramPipelineHash:X16} is already queued";
                return false;
            }

            int workerCount = EnsureVulkanPipelineCompileWorkerCount();
            // Keep a small bounded backlog so one visibility scan can publish a
            // useful cohort of cold variants. Capacity equal to the worker count
            // made a dense view reject and rediscover hundreds of variants on
            // successive frames; that repeated preparation dwarfed the actual
            // sub-millisecond driver compiles. The bound still limits teardown,
            // because native creation remains non-cancellable once started.
            int capacity = Math.Clamp(workerCount * 8, 8, 64);
            int activeJobCount = CountActiveVulkanGraphicsPipelineCompileJobs();
            int totalJobCount = _vulkanGraphicsPipelineCompileJobs.Count;
            if (activeJobCount >= capacity)
            {
                rejectReason = $"async Vulkan pipeline compile queue is at capacity ({capacity}; active={activeJobCount}, completed={Math.Max(0, totalJobCount - activeJobCount)})";
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                    EVulkanPipelineTelemetryEvent.QueueRejected,
                    queueDepth: activeJobCount,
                    queueCapacity: capacity);
                return false;
            }

            AnnounceVulkanPipelineCompileQueue(workerCount, capacity);

            VulkanPipelineCompileTask compileTask =
                EnsureVulkanPipelineCompileTask();
            _vulkanGraphicsPipelineProgramCompileJobs.Add(
                request.Key.ProgramPipelineHash,
                request.CompileKey);
            Task<VulkanGraphicsPipelineCompileResult> task =
                compileTask.Enqueue(
                    () => CreateGraphicsPipelineOnWorker(
                        request,
                        BackgroundPipelineCache));

            var job = new VulkanGraphicsPipelineCompileJob(request, task);
            _vulkanGraphicsPipelineCompileJobs[request.CompileKey] = job;

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
                EVulkanPipelineTelemetryEvent.AsyncQueued,
                backgroundCompile: true,
                queueDepth: activeJobCount + 1,
                queueCapacity: capacity);

            job.PublicationTask = task.ContinueWith(
                static (completedTask, state) =>
                {
                    var (manager, completedJob) =
                        ((VulkanPipelineManager Manager, VulkanGraphicsPipelineCompileJob Job))state!;
                    try
                    {
                        manager.PublishCompletedGraphicsPipelineCompile(completedJob);

                        // Publication is generation-driven. Compatible recordings observe the
                        // immutable pipeline-cache generation on their next prepared snapshot;
                        // do not globally dirty unrelated command buffers from a worker callback.
                    }
                    catch
                    {
                    }
                },
                (this, job),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return true;
    }

    private int CountActiveVulkanGraphicsPipelineCompileJobs()
    {
        int count = 0;
        foreach (VulkanGraphicsPipelineCompileJob job in _vulkanGraphicsPipelineCompileJobs.Values)
        {
            if (!job.Task.IsCompleted)
                count++;
        }

        return count;
    }

    private void InspectPipelineCompileHealth()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (VulkanGraphicsPipelineCompileJob job in _vulkanGraphicsPipelineCompileJobs.Values)
        {
            if (job.Task.IsCompleted)
                continue;

            double ageSeconds = Stopwatch.GetElapsedTime(job.QueuedTimestamp, now).TotalSeconds;
            if (ageSeconds >= VulkanPipelineCompileQuarantineSeconds)
            {
                if (Interlocked.Exchange(ref job.WatchdogState, 2) == 2)
                    continue;

                Debug.VulkanWarning(
                    "[Vulkan][PipelineWatchdog] Cold graphics pipeline compile quarantined after {0:F1}s. " +
                    "Sibling variants remain deferred while existing frames continue without the affected draws. " +
                    "pipeline='{1}' program='{2}' programHash=0x{3:X16} taskState={4}.",
                    ageSeconds,
                    job.Request.PipelineName,
                    job.Request.Program.Data.Name ?? "<unnamed program>",
                    job.Request.Key.ProgramPipelineHash,
                    job.Task.Status);
                continue;
            }

            if (ageSeconds < VulkanPipelineCompileWarningSeconds ||
                Interlocked.CompareExchange(ref job.WatchdogState, 1, 0) != 0)
            {
                continue;
            }

            Debug.VulkanWarning(
                "[Vulkan][PipelineWatchdog] Cold graphics pipeline compile has been pending for {0:F1}s. " +
                "pipeline='{1}' program='{2}' programHash=0x{3:X16} taskState={4}.",
                ageSeconds,
                job.Request.PipelineName,
                job.Request.Program.Data.Name ?? "<unnamed program>",
                job.Request.Key.ProgramPipelineHash,
                job.Task.Status);
        }
    }

    private VulkanPipelineCompileTask EnsureVulkanPipelineCompileTask()
    {
        if (_vulkanPipelineCompileTask is not null)
            return _vulkanPipelineCompileTask;

        lock (_vulkanPipelineCompileGateLock)
        {
            _vulkanPipelineCompileTask ??= new VulkanPipelineCompileTask();
            return _vulkanPipelineCompileTask;
        }
    }

    private int EnsureVulkanPipelineCompileWorkerCount()
    {
        int configured = Volatile.Read(ref _vulkanPipelineCompileWorkerCount);
        if (configured > 0)
            return configured;

        configured = ResolveVulkanPipelineCompileWorkerCount();
        Interlocked.CompareExchange(ref _vulkanPipelineCompileWorkerCount, configured, 0);
        return Volatile.Read(ref _vulkanPipelineCompileWorkerCount);
    }

    private static int ResolveVulkanPipelineCompileWorkerCount()
    {
        string? configured = Environment.GetEnvironmentVariable(VulkanPipelineCompileWorkersEnvVar);
        if (int.TryParse(configured, out int envWorkers) && envWorkers > 0)
            return Math.Clamp(envWorkers, 1, 16);

        // NVIDIA's pipeline compiler can contend with queue submission even when called
        // from a background thread. One low-priority cold compiler protects interactive
        // frame pacing; explicit benchmarking may opt into more workers via the env var.
        return 1;
    }

    private void AnnounceVulkanPipelineCompileQueue(int workerCount, int capacity)
    {
        if (Interlocked.Exchange(ref _vulkanPipelineCompileQueueAnnounced, 1) != 0)
            return;

        Debug.Vulkan(
            "[Vulkan] Async graphics pipeline compilation enabled (workers={0}, capacity={1}, defaultWorkers=1, {2}=<unset|1..16>). Cold compilation runs below normal priority without blocking thread-pool workers; explicit worker overrides may reduce interactive frame pacing.",
            workerCount,
            capacity,
            VulkanPipelineCompileWorkersEnvVar);
    }

    internal void DrainPipelineCompileJobsForOwner(long ownerId, string ownerName)
    {
        VulkanGraphicsPipelineCompileJob[] jobs;
        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values
                .Where(job => job.Request.OwnerId == ownerId)];
        }

        DrainPipelineCompileJobs(
            jobs,
            $"mesh renderer '{ownerName}' teardown");
    }

    internal VulkanPipelineCompilationMutationLease AcquireCompilationMutationLease(
        string reason)
    {
        Monitor.Enter(_vulkanPipelineCompileDependencyMutationLock);
        bool outermostMutation =
            _vulkanPipelineCompileDependencyMutationDepth++ == 0;
        try
        {
            if (outermostMutation)
            {
                VulkanGraphicsPipelineCompileJob[] jobs;
                lock (_vulkanGraphicsPipelineCompileJobsLock)
                {
                    Volatile.Write(
                        ref _vulkanPipelineCompileDependencyMutationActive,
                        1);
                    Interlocked.Increment(
                        ref _vulkanPipelineCompileDependencyGeneration);
                    jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values];
                }

                DrainPipelineCompileJobs(jobs, reason);
            }

            return new VulkanPipelineCompilationMutationLease(
                this,
                outermostMutation);
        }
        catch
        {
            ReleaseCompilationMutationLease(outermostMutation);
            throw;
        }
    }

    internal void ReleaseCompilationMutationLease(bool outermostMutation)
    {
        _vulkanPipelineCompileDependencyMutationDepth--;
        if (outermostMutation)
        {
            lock (_vulkanGraphicsPipelineCompileJobsLock)
            {
                Interlocked.Increment(
                    ref _vulkanPipelineCompileDependencyGeneration);
                Volatile.Write(
                    ref _vulkanPipelineCompileDependencyMutationActive,
                    0);
            }
        }

        Monitor.Exit(_vulkanPipelineCompileDependencyMutationLock);
    }

    internal VulkanPipelineCompilationDependencyLease AcquireCompilationDependencyLease()
    {
        Monitor.Enter(_vulkanPipelineCompileDependencyMutationLock);
        try
        {
            if (Volatile.Read(ref _vulkanPipelineCompileDependencyMutationActive) != 0)
            {
                throw new VulkanPipelineCompilationDeferredException(
                    "Shader or pipeline-layout dependencies are being replaced.");
            }

            return new VulkanPipelineCompilationDependencyLease(
                this,
                Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration));
        }
        catch
        {
            Monitor.Exit(_vulkanPipelineCompileDependencyMutationLock);
            throw;
        }
    }

    internal void ReleaseCompilationDependencyLease()
        => Monitor.Exit(_vulkanPipelineCompileDependencyMutationLock);

    internal bool IsCompilationDependencyGenerationCurrent(
        long dependencyGeneration)
        => dependencyGeneration ==
           Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration);

    private void DrainPipelineCompileJobs(
        VulkanGraphicsPipelineCompileJob[] jobs,
        string reason)
    {
        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            try
            {
                job.Task.Wait();
                job.PublicationTask.Wait();
                bool removed;
                lock (_vulkanGraphicsPipelineCompileJobsLock)
                {
                    removed = _vulkanGraphicsPipelineCompileJobs.TryRemove(
                        job.Request.CompileKey,
                        out _);
                    if (removed)
                        ReleaseProgramCompileReservation(job.Request);
                }

                if (removed &&
                    job.Task.GetAwaiter().GetResult() is { Success: true, Pipeline.Handle: not 0 } result)
                {
                    StoreOrRetireSharedGraphicsPipeline(job.Request.Key, result.Pipeline);
                }
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Async pipeline compile job failed while quiescing for {0}: {1}: {2}",
                    reason,
                    ex.GetType().Name,
                    ex.Message);
            }
        }
    }

    internal void DrainPipelineCompileQueueForShutdown()
    {
        Interlocked.Exchange(ref _vulkanPipelineCompileShutdownStarted, 1);

        VulkanGraphicsPipelineCompileJob[] jobs;
        lock (_vulkanGraphicsPipelineCompileJobsLock)
            jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values];

        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            try
            {
                job.Task.Wait();
                job.PublicationTask.Wait();
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Async pipeline compile job failed during shutdown drain: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            lock (_vulkanGraphicsPipelineCompileJobsLock)
            {
                if (!_vulkanGraphicsPipelineCompileJobs.TryRemove(job.Request.CompileKey, out _))
                    continue;

                ReleaseProgramCompileReservation(job.Request);
            }

            if (job.Task.IsCompletedSuccessfully)
            {
                VulkanGraphicsPipelineCompileResult result = job.Task.GetAwaiter().GetResult();
                if (result.Success && result.Pipeline.Handle != 0)
                    StoreOrRetireSharedGraphicsPipeline(job.Request.Key, result.Pipeline);
            }
        }

        lock (_vulkanGraphicsPipelineCompileJobsLock)
            _vulkanGraphicsPipelineProgramCompileJobs.Clear();

        _vulkanPipelineCompileGate?.Dispose();
        _vulkanPipelineCompileGate = null;
        _vulkanPipelineCompileTask?.Dispose();
        _vulkanPipelineCompileTask = null;
    }
}
