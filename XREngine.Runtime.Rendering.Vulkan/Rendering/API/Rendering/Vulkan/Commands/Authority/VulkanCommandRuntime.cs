using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent owner for command scheduling, recording admission, and
/// persistent schedule artifacts. Native command execution remains supplied by
/// the facade at the call boundary.
/// </summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    private VulkanDeviceContext? _configuredDeviceContext;
    private VulkanResourceRuntime? _configuredResourceRuntime;
    private VulkanFrameTelemetry? _configuredFrameTelemetry;
    private VulkanTrackedCommandEncoder? _primaryCommandEncoder;
    private VulkanQueryCommandService? _queryCommandService;
    private CommandChainSchedule?[]? _scheduleCache;
    private readonly Dictionary<Type, object> _threadWorkspaces = [];
    private readonly object _threadWorkspacesGate = new();
    private readonly VulkanFrameOperationScheduler _primaryOperationScheduler = new();

    internal VulkanProducerCompleteIndirectStream? PendingProducerCompleteIndirectStream { get; set; }
    internal bool ThreadLocalScratchDisposed { get; set; }

    public VulkanCommandScheduler Scheduler { get; } = new();
    public VulkanCommandRecorder Recorder { get; } = new();
    public VulkanCommandWorkerSynchronization Workers { get; } = new();
    public VulkanCommandPoolAuthority Pools { get; } = new();
    public VulkanCommandChainState CommandChains { get; } = new();
    public VulkanCommandBufferState CommandBuffers { get; } = new();
    public VulkanStateTracker StateTracker { get; } = new();
    public VulkanCommandSynchronizationState Synchronization { get; } = new();
    public VulkanDynamicUiBatchTextOverlayRecorder DynamicUiOverlayRecorder { get; } = new();
    public VulkanOpenXrCommandRecordingService OpenXrRecording { get; } = new();
    public VulkanOpenXrEyeWorkerCommandService OpenXrEyeWorkers { get; } = new();

    /// <summary>
    /// Publishes the stable authorities used by native command encoding. Output
    /// and planner authorities are deliberately absent: their frame-local facts
    /// arrive only through frozen prepared inputs.
    /// </summary>
    internal void ConfigurePrimaryRecording(
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(resourceRuntime);
        ArgumentNullException.ThrowIfNull(telemetry);

        if (_configuredDeviceContext is not null &&
            (!ReferenceEquals(_configuredDeviceContext, deviceContext) ||
             !ReferenceEquals(_configuredResourceRuntime, resourceRuntime) ||
             !ReferenceEquals(_configuredFrameTelemetry, telemetry)))
        {
            throw new InvalidOperationException(
                "The Vulkan command runtime cannot be rebound to different authorities.");
        }

        _configuredDeviceContext = deviceContext;
        _configuredResourceRuntime = resourceRuntime;
        _configuredFrameTelemetry = telemetry;
        resourceRuntime.ConfigureLifetimeCommandRuntime(this);
        _queryCommandService ??= new VulkanQueryCommandService(this);
        resourceRuntime.Queries.BindCommands(_queryCommandService);
        OpenXrRecording.Configure(this, resourceRuntime, deviceContext);
        OpenXrEyeWorkers.Configure(this, OpenXrRecording, deviceContext);
    }

    private VulkanDeviceContext DeviceContext
        => _configuredDeviceContext ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    private VulkanResourceRuntime ResourceRuntime
        => _configuredResourceRuntime ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    private VulkanFrameTelemetry FrameTelemetry
        => _configuredFrameTelemetry ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    private VulkanTrackedCommandEncoder PrimaryCommandEncoder
        => _primaryCommandEncoder ??= new VulkanTrackedCommandEncoder(
            DeviceContext.Api,
            DeviceContext,
            this,
            ResourceRuntime,
            FrameTelemetry);

    private Vk Api => DeviceContext.Api;
    private Vk VulkanApi => Api;
    private VulkanCommandRuntime _commandRuntime => this;
    private VulkanDeviceContext _deviceContext => DeviceContext;
    private VulkanResourceRuntime _resourceRuntime => ResourceRuntime;
    private VulkanFrameTelemetry _frameTelemetry => FrameTelemetry;

    /// <summary>
    /// Admits and begins a command-buffer recording without exposing device lifetime
    /// state to the command encoder.
    /// </summary>
    internal void BeginRecording(
        Vk api,
        VulkanDeviceStateMachine deviceState,
        CommandBuffer commandBuffer,
        string operation,
        CommandBufferUsageFlags flags = 0)
    {
        if (!deviceState.IsOperational)
        {
            throw new InvalidOperationException(
                $"Cannot start Vulkan operation '{operation}' while device state is {deviceState.State}.");
        }

        Recorder.Begin(api, commandBuffer, flags);
    }

    /// <summary>
    /// Resets renderer-independent bind and dependency state for a newly begun
    /// command buffer. The caller supplies the encoder that owns lifetime
    /// tracking, so no renderer facade participates in the recording path.
    /// </summary>
    internal void ResetBindState(VulkanTrackedCommandEncoder encoder, CommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        CommandBufferBindState state = new()
        {
            RecordingGeneration = unchecked((ulong)Interlocked.Increment(ref CommandBuffers.RecordingGeneration)),
        };
        lock (CommandBuffers.BindStateGate)
            CommandBuffers.BindStates[handle] = state;
        encoder.BeginTracking(commandBuffer);
    }

    /// <summary>Returns a generation-owned graphics command pool for the calling thread.</summary>
    internal unsafe CommandPool GetThreadGraphicsCommandPool(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resources)
    {
        if (!deviceContext.IsOperational)
            throw new InvalidOperationException($"Cannot create a command pool while device state is {deviceContext.State}.");

        int threadId = Environment.CurrentManagedThreadId;
        lock (Pools.Gate)
        {
            if (Pools.GraphicsByThread.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                return pool;

            uint queueFamily = deviceContext.QueueFamilies.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            CommandPoolCreateInfo createInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
            };
            if (api.CreateCommandPool(deviceContext.Device, ref createInfo, null, out pool) != Result.Success)
                throw new InvalidOperationException("Failed to create a Vulkan graphics command pool.");

            resources.Lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.CommandPool, pool.Handle),
                $"CommandPool.QueueFamily.{queueFamily}",
                externallyOwned: false);
            Pools.GraphicsByThread[threadId] = pool;
            if (Pools.PrimaryGraphics.Handle == 0)
                Pools.PrimaryGraphics = pool;
            return pool;
        }
    }

    internal CommandBuffer AllocateTrackedCommandBuffer(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resources,
        CommandPool pool,
        CommandBufferLevel level,
        string owner)
    {
        if (!deviceContext.IsOperational)
            throw new InvalidOperationException($"Cannot allocate a command buffer while device state is {deviceContext.State}.");
        if (pool.Handle == 0)
            throw new ArgumentException("A live command pool is required.", nameof(pool));

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = level,
            CommandBufferCount = 1,
        };
        if (api.AllocateCommandBuffers(deviceContext.Device, ref allocateInfo, out CommandBuffer commandBuffer) != Result.Success ||
            commandBuffer.Handle == 0)
        {
            throw new InvalidOperationException($"Failed to allocate Vulkan command buffer for {owner}.");
        }

        resources.RegisterSynchronousCommandBuffer(
            commandBuffer,
            pool,
            level,
            owner);
        return commandBuffer;
    }

    /// <summary>
    /// Performs the native portion of a tracked queue submission under the
    /// command authority's device-admission and queue-serialization boundary.
    /// Lifetime and image-state publication are intentionally owned by the
    /// resource and synchronization authorities immediately around this call.
    /// </summary>
    internal Result SubmitToQueueTracked(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanFrameTelemetry telemetry,
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        string operation)
    {
        if (!deviceContext.IsOperational)
        {
            Synchronization.RecordQueueOperation(
                deviceContext.State,
                "submit-rejected",
                queue,
                Result.ErrorDeviceLost,
                0,
                operation);
            return Result.ErrorDeviceLost;
        }

        using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
            CommandBuffers.OneTimeSubmitGate,
            deviceContext.StateMachine,
            telemetry);
        if (!queueOperation.Acquired)
        {
            Synchronization.RecordQueueOperation(
                deviceContext.State,
                "submit-rejected",
                queue,
                Result.ErrorDeviceLost,
                0,
                operation);
            return Result.ErrorDeviceLost;
        }

        Result result;
        using (VulkanCpuStageScope cpuStage = new(telemetry, EVulkanCpuStage.QueueSubmit))
            result = api.QueueSubmit(queue, 1, ref submitInfo, fence);

        deviceContext.ObserveNativeResult(operation, result);
        Synchronization.RecordQueueOperation(
            deviceContext.State,
            "submit",
            queue,
            result,
            0,
            operation);
        if (result == Result.Success)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
        return result;
    }

    internal VulkanExactInvalidationResult InvalidateCachedCommandBuffers(
        ReadOnlySpan<ulong> dependentCommandBuffers,
        string reason)
        => InvalidateCachedCommandBuffersCore(
            dependentCommandBuffers,
            reason,
            FrameTelemetry);

    private VulkanExactInvalidationResult InvalidateCachedCommandBuffersCore(
        ReadOnlySpan<ulong> dependentCommandBuffers,
        string reason,
        VulkanFrameTelemetry telemetry)
    {
        using VulkanCpuStageScope dirtyPropagationStage =
            new(telemetry, EVulkanCpuStage.CommandDirtyPropagation);
        if (dependentCommandBuffers.IsEmpty)
            return default;

        for (int index = 0; index < dependentCommandBuffers.Length; index++)
            if (dependentCommandBuffers[index] != 0)
                CommandBuffers.InvalidatedBuffersPendingReset.TryAdd(
                    dependentCommandBuffers[index],
                    0);

        int exactVariantsDirtied = 0;
        int exactChainsDirtied = 0;
        int unrelatedVariantsPreserved = 0;
        if (CommandBuffers.PrimaryOwners is not null)
        {
            for (int index = 0; index < CommandBuffers.PrimaryOwners.Length; index++)
            {
                PrimaryCommandArtifactOwner owner = CommandBuffers.PrimaryOwners[index];
                bool dependent = ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.PrimaryCommandBuffer.Handle)) ||
                    ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.DynamicUiSecondaryCommandBuffer.Handle));
                if (!dependent)
                {
                    unrelatedVariantsPreserved++;
                    continue;
                }

                if (!owner.Dirty)
                    exactVariantsDirtied++;
                owner.Dirty = true;
                owner.DirtyReason = reason;
            }
        }

        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            foreach (PrimaryCommandArtifactOwner owner in
                     CommandBuffers.OpenXrPrimaryOwners.Values)
            {
                if (!ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.PrimaryCommandBuffer.Handle)))
                {
                    unrelatedVariantsPreserved++;
                    continue;
                }

                if (!owner.Dirty)
                    exactVariantsDirtied++;
                owner.Dirty = true;
                owner.DirtyReason = reason;
            }
        }

        if (CommandChains.Caches is not null)
        {
            for (int cacheIndex = 0;
                 cacheIndex < CommandChains.Caches.Length;
                 cacheIndex++)
            {
                foreach (CommandChain chain in CommandChains.Caches[cacheIndex].Values)
                {
                    if (!ContainsHandle(
                            dependentCommandBuffers,
                            unchecked((ulong)chain.SecondaryCommandBuffer.Handle)))
                    {
                        continue;
                    }

                    chain.State = CommandChainState.Unrecorded;
                    chain.RecordedArtifact.Invalidate(
                        EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                    chain.DirtyReason = CommandChainDirtyReason.ResourcePlan;
                    exactChainsDirtied++;
                }
            }
        }

        return new VulkanExactInvalidationResult(
            exactVariantsDirtied,
            exactChainsDirtied,
            unrelatedVariantsPreserved,
            GlobalFallbackInvalidations: 0);
    }

    internal void DrainInvalidatedCommandBufferRecordings(
        Vk api,
        VulkanResourceRuntime resourceRuntime,
        int maxItems = 64)
    {
        if (maxItems <= 0 || CommandBuffers.InvalidatedBuffersPendingReset.IsEmpty)
            return;

        int resetCount = 0;
        foreach (KeyValuePair<ulong, byte> pair in
                 CommandBuffers.InvalidatedBuffersPendingReset)
        {
            if (resetCount >= maxItems)
                break;

            ulong handle = pair.Key;
            CommandBuffer commandBuffer = new()
            {
                Handle = unchecked((nint)handle),
            };
            if (!resourceRuntime.CanResetCommandBuffer(this, commandBuffer))
                continue;

            Result result = api.ResetCommandBuffer(commandBuffer, 0);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
            if (result != Result.Success)
                continue;

            resourceRuntime.CompleteCommandBufferReset(handle);
            CommandBuffers.TrackingBatches.TryRemove(handle, out _);
            lock (Synchronization._vulkanImageLayoutLock)
                Synchronization._recordedImageLayoutsByCommandBuffer.Remove(handle);
            CommandBuffers.InvalidatedBuffersPendingReset.TryRemove(handle, out _);
            resetCount++;
        }
    }

    internal unsafe void DrainRetiredCommandBuffers(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        int maxItems = 128)
    {
        List<RetiredCommandBuffer> list =
            resourceRuntime.Lifetime.Retirement.CommandBuffers[frameSlot];
        List<RetiredCommandBuffer> ready = [];
        lock (resourceRuntime.Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredCommandBuffer candidate = list[index];
                if (!resourceRuntime.IsCommandBufferRetirementReady(
                        this,
                        candidate.CommandBuffer,
                        candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    unchecked((ulong)candidate.CommandBuffer.Handle),
                    resourceRuntime.Lifetime.Retirement.CommandBufferHandles,
                    resourceRuntime.Lifetime.Retirement.AllCommandBufferHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            RetiredCommandBuffer entry = ready[index];
            CommandBuffer commandBuffer = entry.CommandBuffer;
            lock (Pools.Gate)
                api.FreeCommandBuffers(
                    device,
                    entry.CommandPool,
                    1,
                    &commandBuffer);

            RemoveCommandBufferState(entry.CommandBuffer);
            resourceRuntime.CompleteCommandBufferDestruction(
                entry.CommandBuffer);
            if (CommandBuffers.TryReleaseOwnedSecondaryCommandBuffer(
                    entry.CommandPool,
                    entry.CommandBuffer,
                    out CommandPool poolReadyForRetirement))
            {
                resourceRuntime.QueueCommandPoolRetirement(
                    poolReadyForRetirement,
                    frameSlot);
            }
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            commandBuffers: ready.Count);
    }

    internal unsafe void DrainRetiredCommandPools(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        int maxItems = 16)
    {
        List<RetiredCommandPool> list =
            resourceRuntime.Lifetime.Retirement.CommandPools[frameSlot];
        List<RetiredCommandPool> ready = [];
        lock (resourceRuntime.Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredCommandPool candidate = list[index];
                if (!resourceRuntime.Lifetime.Tracker.IsRetirementReady(
                        candidate.Ticket) ||
                    !resourceRuntime.AreCommandPoolChildrenRetirementReady(
                        this,
                        candidate.CommandPool))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.CommandPool.Handle,
                    resourceRuntime.Lifetime.Retirement.CommandPoolHandles,
                    resourceRuntime.Lifetime.Retirement.AllCommandPoolHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            CommandPool pool = ready[index].CommandPool;
            lock (Pools.Gate)
                api.DestroyCommandPool(device, pool, null);
            resourceRuntime.CompleteCommandPoolChildDestructions(
                this,
                pool);
            resourceRuntime.CompleteCommandPoolDestruction(pool);
        }
    }

    internal unsafe void FreeTrackedCommandBuffer(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        if (commandPool.Handle == 0 || commandBuffer.Handle == 0)
            return;

        CommandBuffer retiring = commandBuffer;
        VulkanRetirementTicket ticket =
            resourceRuntime.PrepareCommandBufferRetirement(
                this,
                retiring,
                owner);
        if (!resourceRuntime.IsCommandBufferRetirementReady(
                this,
                retiring,
                ticket))
        {
            resourceRuntime.QueueCommandBufferRetirement(
                commandPool,
                retiring,
                ticket,
                frameSlot);
            commandBuffer = default;
            return;
        }

        lock (Pools.Gate)
            api.FreeCommandBuffers(device, commandPool, 1, &retiring);
        RemoveCommandBufferState(retiring);
        resourceRuntime.CompleteCommandBufferDestruction(retiring);
        if (CommandBuffers.TryReleaseOwnedSecondaryCommandBuffer(
                commandPool,
                retiring,
                out CommandPool poolReadyForRetirement))
        {
            resourceRuntime.QueueCommandPoolRetirement(
                poolReadyForRetirement,
                frameSlot);
        }

        commandBuffer = default;
    }

    internal void RemoveCommandBufferState(CommandBuffer commandBuffer)
    {
        CommandBuffers.RemoveBindState(commandBuffer);
        Synchronization.RemoveRecordedImageLayouts(commandBuffer);
    }

    /// <summary>
    /// Executes the common worker lifecycle for an already-frozen recording batch.
    /// </summary>
    internal void ExecuteCommandChainRecordingWorker(CommandChainRecordingWorkerState worker)
    {
        VulkanCommandChainRecordingBatch? batch = worker.Batch;
        VulkanPreparedWorkerRecordingContext? context = batch?.PreparedWorkerContext;
        if (batch is null || context?.Telemetry is null)
            return;

        using VulkanCpuStageScope cpuStage = new(context.Telemetry, EVulkanCpuStage.SecondaryRecording);
        long workerStart = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref batch.WorkersStarted);
        UpdateMinimum(ref batch.FirstWorkerStartTimestamp, workerStart);
        UpdateMaximum(ref batch.MaximumQueueDelayTimestamp, workerStart - batch.DispatchTimestamp);
        int concurrentWorkers = Interlocked.Increment(ref batch.ConcurrentWorkers);
        UpdateMaximum(ref batch.PeakConcurrentWorkers, concurrentWorkers);
        try
        {
            worker.LastFrameId = context.FrameId;
            for (int jobIndex = 0; jobIndex < batch.JobCount; jobIndex++)
            {
                if (Volatile.Read(ref batch.Error) is not null ||
                    Volatile.Read(ref batch.CancelRequested) != 0)
                    break;
                if (batch.RecordJobWorkerIndices[jobIndex] != worker.WorkerIndex)
                    continue;

                try
                {
                    RecordPreparedMeshCommandChain(
                        batch,
                        batch.RecordJobChainIndices[jobIndex]);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref batch.Error, ex, null);
                    break;
                }
            }
        }
        finally
        {
            long workerCompletion = Stopwatch.GetTimestamp();
            Interlocked.Add(ref batch.WorkerRecordTimestampTotal, workerCompletion - workerStart);
            UpdateMaximum(ref batch.LastWorkerCompletionTimestamp, workerCompletion);
            Interlocked.Decrement(ref batch.ConcurrentWorkers);
            Interlocked.Increment(ref batch.WorkersCompleted);
            worker.Batch = null;
            bool lastWorker = Workers.Countdown.Signal();
            if (lastWorker)
            {
                Volatile.Write(ref Workers.ActiveWorkerCount, 0);
                Workers.Idle.Set();
                if (batch.Abandoned)
                    batch.ClearReferences();
            }
        }
    }

    internal void DeferSecondaryCommandBufferFree(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        uint imageIndex,
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        string owner)
    {
        ulong generation = resourceRuntime.GetPublishedGeneration(
            ObjectType.CommandBuffer,
            unchecked((ulong)commandBuffer.Handle));
        if (CommandBuffers.TryDeferSecondaryCommandBufferFree(
                imageIndex,
                commandPool,
                commandBuffer,
                generation))
        {
            return;
        }

        FreeTrackedCommandBuffer(
            api,
            device,
            resourceRuntime,
            frameSlot,
            commandPool,
            ref commandBuffer,
            owner);
    }

    private static bool ContainsHandle(
        ReadOnlySpan<ulong> handles,
        ulong candidate)
    {
        if (candidate == 0)
            return false;

        for (int index = 0; index < handles.Length; index++)
            if (handles[index] == candidate)
                return true;

        return false;
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMinimum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate < current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    public CommandChainSchedule? GetReusableSchedule(int slot, int slotCount)
    {
        EnsureScheduleCache(slotCount);
        return (uint)slot < (uint)_scheduleCache!.Length ? _scheduleCache[slot] : null;
    }

    public void CacheSchedule(int slot, int slotCount, CommandChainSchedule schedule)
    {
        EnsureScheduleCache(slotCount);
        if ((uint)slot < (uint)_scheduleCache!.Length)
            _scheduleCache[slot] = schedule;
    }

    public void InvalidateScheduleCache()
    {
        if (_scheduleCache is not null)
            Array.Clear(_scheduleCache);
    }

    public void ReleaseScheduleCache() => _scheduleCache = null;

    public VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>
        GetThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>()
        where TRenderState : class
        where TPlannerState : struct
        where TSwitchingState : class
        where TFrameBuffer : class
        where TReadBuffer : struct
    {
        Type key = typeof(VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>);
        lock (_threadWorkspacesGate)
        {
            if (_threadWorkspaces.TryGetValue(key, out object? workspace))
                return (VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>)workspace;

            var created = new VulkanCommandThreadWorkspace<TRenderState, TPlannerState, TSwitchingState, TFrameBuffer, TReadBuffer>();
            _threadWorkspaces.Add(key, created);
            return created;
        }
    }

    public int ResolveParallelRecordingBucket(
        in VulkanMeshFrameDataRendererFamilyKey rendererFamily,
        int workerCount)
    {
        if (workerCount <= 1)
            return 0;

        int rendererIdentity = RuntimeHelpers.GetHashCode(rendererFamily.Renderer);
        return unchecked((int)((uint)rendererIdentity % (uint)workerCount));
    }

    private void EnsureScheduleCache(int slotCount)
    {
        int count = Math.Max(slotCount, 1);
        if (_scheduleCache is not null && _scheduleCache.Length == count)
            return;

        _scheduleCache = new CommandChainSchedule?[count];
    }
}

internal readonly record struct VulkanProducerCompleteIndirectStream(
    XRDataBuffer IndirectBuffer,
    XRDataBuffer? ParameterBuffer,
    ulong IndirectBufferIdentity,
    ulong ParameterBufferIdentity);

/// <summary>Owns primary and per-thread native command-pool identities.</summary>
internal sealed class VulkanCommandPoolAuthority
{
    internal object Gate { get; } = new();
    internal Dictionary<int, CommandPool> GraphicsByThread { get; } = new();
    internal Dictionary<int, CommandPool> TransferByThread { get; } = new();
    internal CommandPool PrimaryGraphics { get; set; }
    internal CommandPool PrimaryTransfer { get; set; }
}

/// <summary>Persistent worker synchronization state, isolated from renderer-owned recording logic.</summary>
internal sealed class VulkanCommandWorkerSynchronization
{
    internal object Gate { get; } = new();
    internal ManualResetEventSlim Idle { get; } = new(initialState: true);
    internal CountdownEvent Countdown { get; } = new(initialCount: 1);
    internal int Generation;
    internal int ActiveWorkerCount;
    internal int Faulted;
    internal VulkanCommandChainRecordingBatch Batch { get; set; } = new();
    internal CommandChainRecordingWorkerState[]? WorkerStates { get; set; }
}
