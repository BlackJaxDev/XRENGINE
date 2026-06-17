using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanPipelineCompileWorkersEnvVar = "XRE_VK_PIPELINE_COMPILE_WORKERS";

    private readonly ConcurrentDictionary<VkMeshRenderer.GraphicsPipelineCompileKey, VulkanGraphicsPipelineCompileJob> _vulkanGraphicsPipelineCompileJobs = new();
    private readonly Lock _vulkanGraphicsPipelineCompileJobsLock = new();
    private readonly Lock _vulkanPipelineCompileGateLock = new();
    private SemaphoreSlim? _vulkanPipelineCompileGate;
    private int _vulkanPipelineCompileWorkerCount;
    private int _vulkanPipelineCompileQueueAnnounced;

    internal readonly record struct VulkanGraphicsPipelineCompileResult(
        bool Success,
        Pipeline Pipeline,
        string? ErrorMessage,
        double CompileMilliseconds);

    private sealed class VulkanGraphicsPipelineCompileJob(
        VkMeshRenderer.GraphicsPipelineBuildRequest request,
        Task<VulkanGraphicsPipelineCompileResult> task)
    {
        public VkMeshRenderer.GraphicsPipelineBuildRequest Request { get; } = request;
        public Task<VulkanGraphicsPipelineCompileResult> Task { get; } = task;
    }

    internal bool IsVulkanPipelineAsyncCompilationEnabled
        => RuntimeEngine.Rendering.Settings.AsyncProgramCompilation && IsLogicalDeviceReady;

    internal bool TryTakeCompletedVulkanGraphicsPipeline(
        in VkMeshRenderer.GraphicsPipelineCompileKey key,
        out VulkanGraphicsPipelineCompileResult result)
    {
        result = default;
        if (!_vulkanGraphicsPipelineCompileJobs.TryGetValue(key, out VulkanGraphicsPipelineCompileJob? job) ||
            !job.Task.IsCompleted)
        {
            return false;
        }

        if (!_vulkanGraphicsPipelineCompileJobs.TryRemove(key, out job))
            return false;

        result = job.Task.GetAwaiter().GetResult();
        return true;
    }

    internal bool IsVulkanGraphicsPipelineCompileInFlight(in VkMeshRenderer.GraphicsPipelineCompileKey key)
        => _vulkanGraphicsPipelineCompileJobs.ContainsKey(key);

    internal bool TryEnqueueVulkanGraphicsPipelineCompile(
        VkMeshRenderer.GraphicsPipelineBuildRequest request,
        out string rejectReason)
    {
        rejectReason = string.Empty;
        if (!IsVulkanPipelineAsyncCompilationEnabled)
        {
            rejectReason = "async Vulkan pipeline compilation is disabled";
            return false;
        }

        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            int workerCount = EnsureVulkanPipelineCompileWorkerCount();
            int capacity = Math.Max(workerCount, RuntimeEngine.Rendering.Settings.MaxAsyncShaderProgramsPerFrame);
            int activeJobCount = CountActiveVulkanGraphicsPipelineCompileJobs();
            int totalJobCount = _vulkanGraphicsPipelineCompileJobs.Count;
            if (activeJobCount >= capacity)
            {
                rejectReason = $"async Vulkan pipeline compile queue is at capacity ({capacity}; active={activeJobCount}, completed={Math.Max(0, totalJobCount - activeJobCount)})";
                return false;
            }

            if (_vulkanGraphicsPipelineCompileJobs.ContainsKey(request.CompileKey))
            {
                rejectReason = "pipeline compile job is already queued";
                return false;
            }

            AnnounceVulkanPipelineCompileQueue(workerCount, capacity);

            SemaphoreSlim gate = EnsureVulkanPipelineCompileGate(workerCount);
            Task<VulkanGraphicsPipelineCompileResult> task = Task.Factory.StartNew(
                static state =>
                {
                    var (renderer, buildRequest, compileGate) =
                        ((VulkanRenderer Renderer, VkMeshRenderer.GraphicsPipelineBuildRequest Request, SemaphoreSlim Gate))state!;
                    compileGate.Wait();
                    try
                    {
                        return renderer.CreateVulkanGraphicsPipelineOnWorker(buildRequest);
                    }
                    finally
                    {
                        compileGate.Release();
                    }
                },
                (this, request, gate),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            var job = new VulkanGraphicsPipelineCompileJob(request, task);
            if (!_vulkanGraphicsPipelineCompileJobs.TryAdd(request.CompileKey, job))
            {
                rejectReason = "pipeline compile job is already queued";
                return false;
            }

            _ = task.ContinueWith(
                _ =>
                {
                    try
                    {
                        MarkCommandBuffersDirty();
                    }
                    catch
                    {
                    }
                },
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

    private VulkanGraphicsPipelineCompileResult CreateVulkanGraphicsPipelineOnWorker(
        VkMeshRenderer.GraphicsPipelineBuildRequest request)
    {
        long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Pipeline pipeline = request.Owner.CreateGraphicsPipelineFromRequest(
                request,
                pipelineCache: default,
                backgroundCompile: true);
            double elapsedMs = global::System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Debug.Vulkan(
                "[Vulkan] Async graphics pipeline compiled in {0:F2} ms: pipeline='{1}' program='{2}' handle=0x{3:X}.",
                elapsedMs,
                request.PipelineName,
                request.Program.Data.Name ?? "<unnamed program>",
                pipeline.Handle);
            return new VulkanGraphicsPipelineCompileResult(true, pipeline, null, elapsedMs);
        }
        catch (Exception ex)
        {
            double elapsedMs = global::System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
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

        int processorBased = Math.Max(1, Environment.ProcessorCount / 2);
        return Math.Clamp(processorBased, 1, 4);
    }

    private void AnnounceVulkanPipelineCompileQueue(int workerCount, int capacity)
    {
        if (Interlocked.Exchange(ref _vulkanPipelineCompileQueueAnnounced, 1) != 0)
            return;

        Debug.Vulkan(
            "[Vulkan] Async graphics pipeline compilation enabled (workers={0}, capacity={1}, {2}=<unset|1..16>). Background workers create pipelines without the shared VkPipelineCache so parallel vkCreateGraphicsPipelines calls do not require cache synchronization.",
            workerCount,
            capacity,
            VulkanPipelineCompileWorkersEnvVar);
    }

    internal void DrainVulkanPipelineCompileJobsForOwner(VkMeshRenderer owner)
    {
        VulkanGraphicsPipelineCompileJob[] jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values
            .Where(job => ReferenceEquals(job.Request.Owner, owner))];

        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            try
            {
                job.Task.Wait();
                if (_vulkanGraphicsPipelineCompileJobs.TryRemove(job.Request.CompileKey, out _) &&
                    job.Task.GetAwaiter().GetResult() is { Success: true, Pipeline.Handle: not 0 } result)
                {
                    RetirePipeline(result.Pipeline);
                }
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Ignored async pipeline compile job during renderer teardown: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void DrainVulkanPipelineCompileQueueForShutdown()
    {
        VulkanGraphicsPipelineCompileJob[] jobs = [.. _vulkanGraphicsPipelineCompileJobs.Values];
        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            try
            {
                job.Task.Wait();
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[Vulkan] Async pipeline compile job failed during shutdown drain: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (VulkanGraphicsPipelineCompileJob job in jobs)
        {
            if (!_vulkanGraphicsPipelineCompileJobs.TryRemove(job.Request.CompileKey, out _))
                continue;

            if (!job.Task.IsCompletedSuccessfully)
                continue;

            VulkanGraphicsPipelineCompileResult result = job.Task.GetAwaiter().GetResult();
            if (result.Success && result.Pipeline.Handle != 0)
                RetirePipeline(result.Pipeline);
        }

        _vulkanPipelineCompileGate?.Dispose();
        _vulkanPipelineCompileGate = null;
    }
}
