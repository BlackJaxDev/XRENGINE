using System.Runtime.CompilerServices;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent owner for command scheduling, recording admission, and
/// persistent schedule artifacts. Native command execution remains supplied by
/// the facade at the call boundary.
/// </summary>
internal sealed class VulkanCommandRuntime
{
    private CommandChainSchedule?[]? _scheduleCache;
    private readonly Dictionary<Type, object> _threadWorkspaces = [];
    private readonly object _threadWorkspacesGate = new();

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

    internal VulkanExactInvalidationResult InvalidateCachedCommandBuffers(
        ReadOnlySpan<ulong> dependentCommandBuffers,
        string reason,
        VulkanOutputRuntime outputRuntime,
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

        lock (outputRuntime.OpenXrBackend.PrimaryCommandArtifactOwnersLock)
        {
            Dictionary<ulong, PrimaryCommandArtifactOwner> owners =
                outputRuntime.OpenXrBackend
                    .GetPrimaryCommandArtifactOwners<PrimaryCommandArtifactOwner>();
            foreach (PrimaryCommandArtifactOwner owner in owners.Values)
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
