using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanPipelineCompileWorkersEnvVar = XREngineEnvironmentVariables.VulkanPipelineCompileWorkers;
    private const double VulkanPipelineCompileWarningSeconds = 2.0;
    private const double VulkanPipelineCompileQuarantineSeconds = 10.0;

    private readonly ConcurrentDictionary<VkMeshRenderer.GraphicsPipelineCompileKey, VulkanGraphicsPipelineCompileJob> _vulkanGraphicsPipelineCompileJobs = new();
    private readonly Dictionary<ulong, VkMeshRenderer.GraphicsPipelineCompileKey> _vulkanGraphicsPipelineProgramCompileJobs = new();
    private readonly Lock _vulkanGraphicsPipelineCompileJobsLock = new();
    private readonly object _vulkanPipelineCompileDependencyMutationLock = new();
    private readonly Lock _vulkanPipelineCompileGateLock = new();
    private SemaphoreSlim? _vulkanPipelineCompileGate;
    private int _vulkanPipelineCompileWorkerCount;
    private int _vulkanPipelineCompileQueueAnnounced;
    private int _vulkanPipelineCompileShutdownStarted;
    private int _vulkanPipelineCompileDependencyMutationActive;
    private int _vulkanPipelineCompileDependencyMutationDepth;
    private long _vulkanPipelineCompileDependencyGeneration;
    private long _vulkanPipelineCompileActivityGeneration;

    internal readonly record struct VulkanGraphicsPipelineCompileResult(
        bool Success,
        Pipeline Pipeline,
        string? ErrorMessage,
        double CompileMilliseconds,
        bool Retryable = false);

    private sealed class VulkanGraphicsPipelineCompileJob(
        VkMeshRenderer.GraphicsPipelineBuildRequest request,
        Task<VulkanGraphicsPipelineCompileResult> task)
    {
        public VkMeshRenderer.GraphicsPipelineBuildRequest Request { get; } = request;
        public Task<VulkanGraphicsPipelineCompileResult> Task { get; } = task;
        public Task PublicationTask { get; set; } = global::System.Threading.Tasks.Task.CompletedTask;
        public long QueuedTimestamp { get; } = Stopwatch.GetTimestamp();
        public int WatchdogState;
    }

    internal bool IsVulkanPipelineAsyncCompilationEnabled
        => RuntimeEngine.Rendering.Settings.AsyncProgramCompilation &&
           IsLogicalDeviceReady &&
           AcceptsBackendWork &&
           Volatile.Read(ref _vulkanPipelineCompileShutdownStarted) == 0;

    internal ulong VulkanPipelineCompileActivityGeneration
        => unchecked((ulong)Volatile.Read(ref _vulkanPipelineCompileActivityGeneration));

    internal long VulkanPipelineCompileDependencyGeneration
        => Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration);

    internal bool TryTakeCompletedVulkanGraphicsPipeline(
        in VkMeshRenderer.GraphicsPipelineCompileKey key,
        out VulkanGraphicsPipelineCompileResult result)
    {
        InspectVulkanPipelineCompileHealth();
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

            ReleaseVulkanPipelineProgramCompileReservation(job.Request);
            Interlocked.Increment(ref _vulkanPipelineCompileActivityGeneration);
            return true;
        }
    }

    internal bool IsVulkanGraphicsPipelineCompileInFlight(in VkMeshRenderer.GraphicsPipelineCompileKey key)
    {
        InspectVulkanPipelineCompileHealth();
        return _vulkanGraphicsPipelineCompileJobs.ContainsKey(key);
    }

    internal bool TryEnqueueVulkanGraphicsPipelineCompile(
        VkMeshRenderer.GraphicsPipelineBuildRequest request,
        out string rejectReason)
    {
        rejectReason = string.Empty;
        InspectVulkanPipelineCompileHealth();
        if (!IsVulkanPipelineAsyncCompilationEnabled)
        {
            rejectReason = "async Vulkan pipeline compilation is disabled";
            return false;
        }

        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            if (Volatile.Read(ref _vulkanPipelineCompileShutdownStarted) != 0 ||
                !AcceptsBackendWork)
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
            // Do not materialize a backlog of native vkCreate* calls. A driver
            // compile is not cancellable, and shutdown must keep the device alive
            // until every started call returns. Limiting capacity to the active
            // worker count bounds teardown latency and lets rejected variants retry
            // after the current compile publishes.
            int capacity = workerCount;
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

            SemaphoreSlim gate = EnsureVulkanPipelineCompileGate(workerCount);
            _vulkanGraphicsPipelineProgramCompileJobs.Add(
                request.Key.ProgramPipelineHash,
                request.CompileKey);
            Task<VulkanGraphicsPipelineCompileResult> task =
                VulkanPipelineCompileTask.RunAsync(
                    gate,
                    () => CreateVulkanGraphicsPipelineOnWorker(request));

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
                    var (renderer, completedJob, pipelineKey) =
                        ((VulkanRenderer Renderer, VulkanGraphicsPipelineCompileJob Job, VkMeshRenderer.PipelineKey PipelineKey))state!;
                    try
                    {
                        lock (renderer._vulkanGraphicsPipelineCompileJobsLock)
                        {
                            if (!renderer._vulkanGraphicsPipelineCompileJobs.TryGetValue(
                                    completedJob.Request.CompileKey,
                                    out VulkanGraphicsPipelineCompileJob? registeredJob) ||
                                !ReferenceEquals(registeredJob, completedJob))
                            {
                                return;
                            }

                            if (completedJob.Task.IsCompletedSuccessfully)
                            {
                                VulkanGraphicsPipelineCompileResult result = completedJob.Task.GetAwaiter().GetResult();
                                if (result.Success && result.Pipeline.Handle != 0)
                                    renderer.StoreOrRetireSharedGraphicsPipeline(pipelineKey, result.Pipeline);
                            }

                            renderer._vulkanGraphicsPipelineCompileJobs.TryRemove(
                                completedJob.Request.CompileKey,
                                out _);
                            renderer.ReleaseVulkanPipelineProgramCompileReservation(completedJob.Request);
                            Interlocked.Increment(ref renderer._vulkanPipelineCompileActivityGeneration);
                        }

                        // Publication is generation-driven. Compatible recordings observe the
                        // immutable pipeline-cache generation on their next prepared snapshot;
                        // do not globally dirty unrelated command buffers from a worker callback.
                    }
                    catch
                    {
                    }
                },
                (this, job, request.Key),
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

    private void InspectVulkanPipelineCompileHealth()
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

    private void ReleaseVulkanPipelineProgramCompileReservation(
        VkMeshRenderer.GraphicsPipelineBuildRequest request)
    {
        if (_vulkanGraphicsPipelineProgramCompileJobs.TryGetValue(
                request.Key.ProgramPipelineHash,
                out VkMeshRenderer.GraphicsPipelineCompileKey compileKey) &&
            compileKey.Equals(request.CompileKey))
        {
            _vulkanGraphicsPipelineProgramCompileJobs.Remove(
                request.Key.ProgramPipelineHash);
        }
    }

    private VulkanGraphicsPipelineCompileResult CreateVulkanGraphicsPipelineOnWorker(
        VkMeshRenderer.GraphicsPipelineBuildRequest request)
    {
        long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Pipeline pipeline = request.Owner.CreateGraphicsPipelineFromRequest(
                request,
                pipelineCache: BackgroundPipelineCache,
                backgroundCompile: true);
            double elapsedMs = global::System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            PublishVulkanBackgroundPipelineCache(elapsedMs);
            uint keyHash = unchecked((uint)request.Key.GetHashCode());
            Debug.Vulkan(
                "[Vulkan] Async graphics pipeline compiled in {0:F2} ms: pipeline='{1}' program='{2}' key=0x{3:X8} programHash=0x{4:X16} vertexLayout=0x{5:X16} descriptorLayout=0x{6:X16} depthTest={7} depthWrite={8} depthCompare={9} blend={10} atc={11} cull={12} handle=0x{13:X}.",
                elapsedMs,
                request.PipelineName,
                request.Program.Data.Name ?? "<unnamed program>",
                keyHash,
                request.Key.ProgramPipelineHash,
                request.Key.VertexLayoutHash,
                request.Key.DescriptorLayoutHash,
                request.Key.DepthTestEnabled,
                request.Key.DepthWriteEnabled,
                request.Key.DepthCompareOp,
                request.Key.BlendEnabled,
                request.Key.AlphaToCoverageEnabled,
                request.Key.CullMode,
                pipeline.Handle);
            return new VulkanGraphicsPipelineCompileResult(true, pipeline, null, elapsedMs);
        }
        catch (VulkanPipelineCompilationDeferredException ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new VulkanGraphicsPipelineCompileResult(
                false,
                default,
                ex.Message,
                elapsedMs,
                Retryable: true);
        }
        catch (Exception ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new VulkanGraphicsPipelineCompileResult(false, default, ex.Message, elapsedMs);
        }
    }

    private SemaphoreSlim EnsureVulkanPipelineCompileGate(int workerCount)
    {
        if (_vulkanPipelineCompileGate is not null)
            return _vulkanPipelineCompileGate;

        lock (_vulkanPipelineCompileGateLock)
        {
            _vulkanPipelineCompileGate ??= new SemaphoreSlim(workerCount, workerCount);
            return _vulkanPipelineCompileGate;
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

    internal void DrainVulkanPipelineCompileJobsForOwner(VkMeshRenderer owner)
    {
        VulkanGraphicsPipelineCompileJob[] jobs;
        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values
                .Where(job => ReferenceEquals(job.Request.Owner, owner))];
        }

        DrainVulkanPipelineCompileJobs(
            jobs,
            $"mesh renderer '{owner.GetDescribingName()}' teardown");
    }

    /// <summary>
    /// Prevents new native graphics-pipeline compiles, drains every request that may
    /// still reference a shader module or pipeline layout, then performs the mutation.
    /// Nested shader/program invalidation on the same thread shares the outer barrier.
    /// </summary>
    internal void ExecuteWithVulkanPipelineCompilationQuiesced(
        Action mutation,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_vulkanPipelineCompileDependencyMutationLock)
        {
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

                    DrainVulkanPipelineCompileJobs(jobs, reason);
                }

                mutation();
            }
            finally
            {
                _vulkanPipelineCompileDependencyMutationDepth--;
                if (outermostMutation)
                {
                    lock (_vulkanGraphicsPipelineCompileJobsLock)
                    {
                        // Requests captured either before or during the mutation must
                        // not enter the driver after the replacement handles publish.
                        Interlocked.Increment(
                            ref _vulkanPipelineCompileDependencyGeneration);
                        Volatile.Write(
                            ref _vulkanPipelineCompileDependencyMutationActive,
                            0);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Captures pointer-bearing shader and pipeline-layout state while dependency
    /// replacement is excluded. The returned generation lets enqueue and worker
    /// entry reject a snapshot if a mutation starts after this method returns.
    /// </summary>
    internal T CaptureVulkanPipelineCompilationDependencies<T>(
        Func<long, T> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        lock (_vulkanPipelineCompileDependencyMutationLock)
        {
            if (Volatile.Read(ref _vulkanPipelineCompileDependencyMutationActive) != 0)
            {
                throw new VulkanPipelineCompilationDeferredException(
                    "Shader or pipeline-layout dependencies are being replaced.");
            }

            long dependencyGeneration =
                Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration);
            T snapshot = capture(dependencyGeneration);
            if (dependencyGeneration !=
                Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration))
            {
                throw new VulkanPipelineCompilationDeferredException(
                    "Shader or pipeline-layout dependencies changed while the pipeline request was captured.");
            }

            return snapshot;
        }
    }

    private bool IsVulkanPipelineCompileDependencyGenerationCurrent(
        long dependencyGeneration)
        => dependencyGeneration ==
           Volatile.Read(ref _vulkanPipelineCompileDependencyGeneration);

    private void DrainVulkanPipelineCompileJobs(
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
                        ReleaseVulkanPipelineProgramCompileReservation(job.Request);
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

    private void DrainVulkanPipelineCompileQueueForShutdown()
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

                ReleaseVulkanPipelineProgramCompileReservation(job.Request);
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
    }

    protected override void OnBackendRetirementBeginning()
        => DrainVulkanPipelineCompileQueueForShutdown();
}
