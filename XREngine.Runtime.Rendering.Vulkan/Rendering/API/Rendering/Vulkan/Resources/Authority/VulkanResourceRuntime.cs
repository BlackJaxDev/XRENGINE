using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Aggregates the mutable resource services for one logical-device lifetime.
/// </summary>
/// <remarks>
/// This type deliberately has no renderer reference. Native Vulkan calls, command recording,
/// and shutdown ordering remain renderer concerns; this object only establishes the single
/// ownership boundary for the state those operations mutate.
/// </remarks>
internal sealed class VulkanResourceRuntime
{
    internal VulkanResourceRuntime(int frameSlotCount)
    {
        BackendObjects = new VulkanBackendObjectRegistry();
        Descriptors = new VulkanDescriptorManager();
        Allocations = new VulkanAllocationAuthority(
            new VulkanBufferResourceManager(),
            new VulkanImageAllocationTracker(),
            new VulkanStagingManager());
        Uploads = new VulkanTextureUploadService();
        Queries = new VulkanQueryAuthority();
        FallbackTexture = new VulkanFallbackTextureState();
        Lifetime = new VulkanLifetimeAuthority(
            new VulkanResourceLifetimeTracker(),
            new VulkanResourceRetirementQueue(frameSlotCount));
    }

    internal VulkanBackendObjectRegistry BackendObjects { get; }
    internal VulkanDescriptorManager Descriptors { get; }
    internal VulkanAllocationAuthority Allocations { get; }
    internal VulkanTextureUploadService Uploads { get; }
    internal VulkanQueryAuthority Queries { get; }
    internal VulkanFallbackTextureState FallbackTexture { get; }
    internal VulkanLifetimeAuthority Lifetime { get; }
    internal VulkanPipelineManager PipelineManager { get; } = new();
    internal VulkanBackendObjectContext? BackendObjectContext;
    internal RenderPass SwapchainRenderPass;
    internal RenderPass SwapchainLoadRenderPass;
    internal Dictionary<ulong, uint> RenderPassColorAttachmentCounts { get; } = new();
    internal Dictionary<ulong, Format[]> RenderPassColorAttachmentFormats { get; } = new();
    internal Dictionary<ulong, string> RenderPassSemanticSignatures { get; } = new();
    internal Dictionary<Format, bool> FormatColorBlendSupport { get; } = new();
    internal bool? SupportsGpuAutoExposure;
    internal bool AutoExposureComputeInitialized;
    internal XRRenderProgram? AutoExposureComputeProgram2D;
    internal XRRenderProgram? AutoExposureComputeProgram2DArray;
    internal object TextureUploadContextSync { get; } = new();
    internal Dictionary<VulkanFrameBufferRenderPassKey, Silk.NET.Vulkan.RenderPass> FrameBufferRenderPasses { get; } = new();
    internal VulkanPhysicalImageGroup? RetainedAutoExposureHistoryGroup;

    internal ulong GetPublishedGeneration(ObjectType type, ulong handle)
        => Lifetime.Tracker.GetPublishedGeneration(
            new VulkanResourceLifetimeKey(type, handle));

    internal void NotifyResourceUseCompleted(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.Pins.ResetCompletion();
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }
    }

    internal bool CanResetCommandBuffer(
        VulkanCommandRuntime commandRuntime,
        CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            if (commandRuntime.CommandBuffers.TrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                    if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                        return false;
            }

            if (Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                if (lifetime.QueuedSubmissionCount != 0)
                    return false;

                VulkanResourceLifetimeKey poolKey = lifetime.AllocatingCommandPool;
                if (poolKey.IsValid &&
                    (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        poolKey,
                        out VulkanResourceLifetimeRecord? pool) ||
                     pool.Generation != lifetime.AllocatingCommandPoolGeneration ||
                     (pool.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                    EVulkanResourceLifetimeState.Destroyed)) != 0))
                {
                    return false;
                }
            }

            VulkanResourceLifetimeRecord commandRecord =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(
                    new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                    "CommandBuffer.Reset");
            if ((commandRecord.State &
                 (EVulkanResourceLifetimeState.PendingRetirement |
                  EVulkanResourceLifetimeState.Destroyed)) != 0 ||
                commandRecord.Pins.HasRecordedReferences)
            {
                return false;
            }

            return UpdateResourceCompletionStateNoLock(commandRecord);
        }
    }

    internal void CompleteCommandBufferReset(ulong handle)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                return;
            }

            ReleaseCommandBufferDependenciesNoLock(handle, lifetime);
            lifetime.FrameDataLease.EvictCachedVariant();
            lifetime.FrameDataLease.Reset();
            lifetime.RecordingGeneration++;
        }
    }

    internal bool IsCommandBufferRetirementReady(
        VulkanCommandRuntime commandRuntime,
        CommandBuffer commandBuffer,
        in VulkanRetirementTicket ticket)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            if (Lifetime.Tracker.ForcedRetirementDrainDepth > 0)
                return true;

            if (commandRuntime.CommandBuffers.TrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                    if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                        return false;
            }

            if (Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                return false;
            }

            return Lifetime.Tracker.IsRetirementReadyNoLock(ticket);
        }
    }

    internal bool AreCommandPoolChildrenRetirementReady(
        VulkanCommandRuntime commandRuntime,
        CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return true;

        VulkanResourceLifetimeKey poolKey = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        CommandBuffer[] children;
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                    poolKey,
                    out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
            {
                return true;
            }

            List<CommandBuffer> trackedChildren = [];
            foreach (ulong childHandle in ownedChildren)
                if (Lifetime.Tracker.CommandBufferLifetimes.ContainsKey(childHandle))
                    trackedChildren.Add(new CommandBuffer
                    {
                        Handle = unchecked((nint)childHandle),
                    });
            children = [.. trackedChildren];
        }

        for (int index = 0; index < children.Length; index++)
        {
            CommandBuffer child = children[index];
            if (IsCommandBufferPendingRetirement(child) ||
                !IsCommandBufferRetirementReady(
                    commandRuntime,
                    child,
                    VulkanRetirementTicket.None))
            {
                return false;
            }
        }

        return true;
    }

    internal void CompleteCommandBufferDestruction(CommandBuffer commandBuffer)
        => CompleteSimpleResourceDestruction(
            ObjectType.CommandBuffer,
            unchecked((ulong)commandBuffer.Handle));

    internal void CompleteCommandPoolDestruction(CommandPool commandPool)
        => CompleteSimpleResourceDestruction(
            ObjectType.CommandPool,
            commandPool.Handle);

    internal void CompleteCommandPoolChildDestructions(
        VulkanCommandRuntime commandRuntime,
        CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey poolKey = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        CommandBuffer[] children;
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                    poolKey,
                    out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
            {
                return;
            }

            children = new CommandBuffer[ownedChildren.Count];
            int index = 0;
            foreach (ulong childHandle in ownedChildren)
                children[index++] = new CommandBuffer
                {
                    Handle = unchecked((nint)childHandle),
                };
        }

        for (int index = 0; index < children.Length; index++)
        {
            commandRuntime.RemoveCommandBufferState(children[index]);
            CompleteCommandBufferDestruction(children[index]);
        }
    }

    internal void QueueCommandPoolRetirement(
        CommandPool commandPool,
        int frameSlot)
    {
        if (commandPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        VulkanRetirementTicket ticket;
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(
                    key,
                    "CommandRuntime.OwnedSecondaryPool");
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else
            {
                UpdateResourceCompletionStateNoLock(resource);
                ticket = new VulkanRetirementTicket(
                    resource.Pins.LastGraphicsSequence,
                    resource.Pins.LastTransferSequence,
                    resource.Pins.LastOtherSequence,
                    Stopwatch.GetTimestamp(),
                    resource.Generation,
                    (resource.State & EVulkanResourceLifetimeState.External) != 0,
                    VulkanRetirementPinSet.Single(key, resource.Generation));
                resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                resource.RetirementTicket = ticket;
                Lifetime.Tracker.PublishedResourceGenerations[key] = 0;
            }
        }

        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                commandPool.Handle,
                new RetiredCommandPool(commandPool, ticket),
                Lifetime.Retirement.CommandPools,
                Lifetime.Retirement.CommandPoolHandles,
                Lifetime.Retirement.AllCommandPoolHandles);
    }

    internal VulkanRetirementTicket PrepareCommandBufferRetirement(
        CommandBuffer commandBuffer,
        string owner)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return VulkanRetirementTicket.None;

        VulkanResourceLifetimeKey key = new(
            ObjectType.CommandBuffer,
            handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                return resource.RetirementTicket;

            UpdateResourceCompletionStateNoLock(resource);
            VulkanRetirementTicket ticket = new(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                resource.Generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, resource.Generation));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            Lifetime.Tracker.PublishedResourceGenerations[key] = 0;
            return ticket;
        }
    }

    internal void QueueCommandBufferRetirement(
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        in VulkanRetirementTicket ticket,
        int frameSlot)
    {
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                unchecked((ulong)commandBuffer.Handle),
                new RetiredCommandBuffer(
                    commandPool,
                    commandBuffer,
                    ticket),
                Lifetime.Retirement.CommandBuffers,
                Lifetime.Retirement.CommandBufferHandles,
                Lifetime.Retirement.AllCommandBufferHandles);
    }

    private bool IsCommandBufferPendingRetirement(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        lock (Lifetime.Retirement.SyncRoot)
            return Lifetime.Retirement.AllCommandBufferHandles.Contains(
                unchecked((ulong)commandBuffer.Handle));
    }

    private bool UpdateResourceCompletionStateNoLock(
        VulkanResourceLifetimeRecord resource)
    {
        bool completed =
            resource.Pins.LastGraphicsSequence <= Lifetime.Tracker.CompletedGraphicsSequence &&
            resource.Pins.LastTransferSequence <= Lifetime.Tracker.CompletedTransferSequence &&
            resource.Pins.LastOtherSequence <= Lifetime.Tracker.CompletedOtherSequence;
        if (!completed)
            return false;

        if ((resource.State & EVulkanResourceLifetimeState.Submitted) != 0)
        {
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }

        return true;
    }

    private void ReleaseCommandBufferDependenciesNoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in
                 lifetime.Dependencies)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != generation)
            {
                continue;
            }

            VulkanRenderer.ReleaseVulkanRecordedGenerationPin(resource);
            if (Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(
                    key,
                    out HashSet<ulong>? commandBuffers))
            {
                commandBuffers.Remove(commandBufferHandle);
            }
        }

        lifetime.Dependencies.Clear();
        lifetime.TouchedDependencies.Clear();
    }

    internal unsafe void DrainRetiredPipelines(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<RetiredPipeline> list = Lifetime.Retirement.Pipelines[frameSlot];
        List<RetiredPipeline> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredPipeline candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.Pipeline.Handle,
                    Lifetime.Retirement.PipelineHandles,
                    Lifetime.Retirement.AllPipelineHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            Pipeline pipeline = ready[index].Pipeline;
            if (pipeline.Handle == 0)
                continue;

            api.DestroyPipeline(device, pipeline, null);
            CompleteSimpleResourceDestruction(
                ObjectType.Pipeline,
                pipeline.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            pipelines: destroyed);
    }

    internal unsafe void DrainRetiredPipelineLayouts(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<VulkanRenderer.RetiredPipelineLayout> list =
            Lifetime.Retirement.PipelineLayouts[frameSlot];
        List<VulkanRenderer.RetiredPipelineLayout> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                VulkanRenderer.RetiredPipelineLayout candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.PipelineLayout.Handle,
                    Lifetime.Retirement.PipelineLayoutHandles,
                    Lifetime.Retirement.AllPipelineLayoutHandles);
                Lifetime.LivePipelineLayoutHandles.TryRemove(
                    candidate.PipelineLayout.Handle,
                    out _);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            PipelineLayout layout = ready[index].PipelineLayout;
            if (layout.Handle == 0)
                continue;

            api.DestroyPipelineLayout(device, layout, null);
            CompleteSimpleResourceDestruction(
                ObjectType.PipelineLayout,
                layout.Handle);
        }
    }

    internal unsafe void DrainRetiredDescriptorSetLayouts(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<VulkanRenderer.RetiredDescriptorSetLayout> list =
            Lifetime.Retirement.DescriptorSetLayouts[frameSlot];
        List<VulkanRenderer.RetiredDescriptorSetLayout> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                VulkanRenderer.RetiredDescriptorSetLayout candidate =
                    list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorSetLayout.Handle,
                    Lifetime.Retirement.DescriptorSetLayoutHandles,
                    Lifetime.Retirement.AllDescriptorSetLayoutHandles);
                Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(
                    candidate.DescriptorSetLayout.Handle,
                    out _);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            DescriptorSetLayout layout = ready[index].DescriptorSetLayout;
            if (layout.Handle == 0)
                continue;

            api.DestroyDescriptorSetLayout(device, layout, null);
            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorSetLayout,
                layout.Handle);
        }
    }

    internal unsafe void DrainRetiredQueryPools(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 32)
    {
        List<RetiredQueryPool> list = Lifetime.Retirement.QueryPools[frameSlot];
        List<RetiredQueryPool> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredQueryPool candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.QueryPool.Handle,
                    Lifetime.Retirement.QueryPoolHandles,
                    Lifetime.Retirement.AllQueryPoolHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            QueryPool queryPool = ready[index].QueryPool;
            api.DestroyQueryPool(device, queryPool, null);
            CompleteSimpleResourceDestruction(
                ObjectType.QueryPool,
                queryPool.Handle);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            queryPools: ready.Count);
    }

    internal unsafe void DrainRetiredBufferViews(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredBufferView> list = Lifetime.Retirement.BufferViews[frameSlot];
        List<RetiredBufferView> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredBufferView candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.BufferView.Handle,
                    Lifetime.Retirement.BufferViewHandles,
                    Lifetime.Retirement.AllBufferViewHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            BufferView bufferView = ready[index].BufferView;
            api.DestroyBufferView(device, bufferView, null);
            CompleteSimpleResourceDestruction(
                ObjectType.BufferView,
                bufferView.Handle);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            bufferViews: ready.Count);
    }

    internal unsafe void DrainRetiredFramebuffers(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredFramebuffer> list = Lifetime.Retirement.Framebuffers[frameSlot];
        List<RetiredFramebuffer> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredFramebuffer candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.Framebuffer.Handle,
                    Lifetime.Retirement.FramebufferHandles,
                    Lifetime.Retirement.AllFramebufferHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            Framebuffer framebuffer = ready[index].Framebuffer;
            if (framebuffer.Handle == 0)
                continue;

            api.DestroyFramebuffer(device, framebuffer, null);
            CompleteSimpleResourceDestruction(
                ObjectType.Framebuffer,
                framebuffer.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            framebuffers: destroyed);
    }

    internal unsafe void DrainRetiredBuffers(
        Vk api,
        Device device,
        VulkanOutputRuntime outputRuntime,
        VulkanFrameTelemetry telemetry,
        int frameSlot,
        int maxItems = 256)
    {
        List<RetiredBuffer> list = Lifetime.Retirement.Buffers[frameSlot];
        List<RetiredBuffer> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredBuffer candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket) ||
                    HasUndestroyedBufferView(candidate.Buffer))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                if (candidate.Buffer.Handle != 0)
                {
                    Lifetime.Retirement.BufferHandles[frameSlot].Remove(
                        candidate.Buffer.Handle);
                    Lifetime.Retirement.AllBufferHandles.Remove(
                        candidate.Buffer.Handle);
                }
                if (candidate.Memory.Handle != 0)
                {
                    Lifetime.Retirement.MemoryHandles[frameSlot].Remove(
                        candidate.Memory.Handle);
                    Lifetime.Retirement.AllMemoryHandles.Remove(
                        candidate.Memory.Handle);
                }
            }
        }

        int destroyedBuffers = 0;
        int freedMemories = 0;
        int pooledBuffers = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            Silk.NET.Vulkan.Buffer buffer = ready[index].Buffer;
            DeviceMemory memory = ready[index].Memory;
            if (buffer.Handle != 0)
            {
                if (memory.Handle != 0 &&
                    Allocations.Staging.TryRelease(buffer, memory))
                {
                    ReactivateResourceAfterRetirement(
                        ObjectType.Buffer,
                        buffer.Handle,
                        "StagingPool.Reuse");
                    pooledBuffers++;
                    continue;
                }

                if (Allocations.Buffers.Allocations.TryRemove(
                        buffer.Handle,
                        out VulkanMemoryAllocation allocation))
                {
                    if (TryTakeLiveBuffer(buffer))
                    {
                        telemetry.UnregisterDeviceAddressRange(buffer);
                        api.DestroyBuffer(device, buffer, null);
                        Allocations.Buffers.MemoryAllocator!.Free(
                            api,
                            device,
                            allocation);
                        CompleteSimpleResourceDestruction(
                            ObjectType.Buffer,
                            buffer.Handle);
                        destroyedBuffers++;
                        freedMemories++;
                    }
                    continue;
                }

                if (Allocations.Buffers.LegacyAllocations.TryRemove(
                        buffer.Handle,
                        out VulkanMemoryAllocation legacyAllocation))
                {
                    if (TryTakeLiveBuffer(buffer))
                    {
                        telemetry.UnregisterDeviceAddressRange(buffer);
                        api.DestroyBuffer(device, buffer, null);
                        if (legacyAllocation.Memory.Handle != 0)
                        {
                            api.FreeMemory(device, legacyAllocation.Memory, null);
                            freedMemories++;
                        }
                        CompleteSimpleResourceDestruction(
                            ObjectType.Buffer,
                            buffer.Handle);
                        destroyedBuffers++;
                    }
                    continue;
                }

                if (TryTakeLiveBuffer(buffer))
                {
                    telemetry.UnregisterDeviceAddressRange(buffer);
                    api.DestroyBuffer(device, buffer, null);
                    CompleteSimpleResourceDestruction(
                        ObjectType.Buffer,
                        buffer.Handle);
                    destroyedBuffers++;
                }
            }

            if (memory.Handle != 0 &&
                Allocations.Buffers.MemoryAllocator is VulkanLegacyAllocator)
            {
                api.FreeMemory(device, memory, null);
                freedMemories++;
            }
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            buffers: destroyedBuffers,
            bufferMemories: freedMemories);
        if (pooledBuffers > 0)
            Allocations.Staging.Trim(outputRuntime);
    }

    internal unsafe void DrainRetiredImages(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredImageResourceEntry> list =
            Lifetime.Retirement.Images[frameSlot];
        List<RetiredImageResourceEntry> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredImageResourceEntry candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket) ||
                    HasUndestroyedImageDependency(candidate.Resources))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
            }
        }

        int destroyedImages = 0;
        int freedMemories = 0;
        int destroyedViews = 0;
        int destroyedSamplers = 0;
        long destroyedImageBytes = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            RetiredImageResourceEntry entry = ready[index];
            RetiredImageResources resources = entry.Resources;
            bool canDestroyImage = resources.Image.Handle != 0 &&
                CanDestroyResourceGeneration(
                    ObjectType.Image,
                    resources.Image.Handle,
                    entry.ImageGeneration);
            bool canDestroySampler = resources.Sampler.Handle != 0 &&
                CanDestroyResourceGeneration(
                    ObjectType.Sampler,
                    resources.Sampler.Handle,
                    entry.SamplerGeneration);
            bool hasTrackedImageAllocation = false;
            VulkanMemoryAllocation trackedImageAllocation = default;
            if (canDestroyImage)
            {
                hasTrackedImageAllocation =
                    Allocations.Images.Allocations.TryRemove(
                        resources.Image.Handle,
                        out trackedImageAllocation);
                Allocations.Images.DebugInfo.TryRemove(
                    resources.Image.Handle,
                    out _);
            }

            if (canDestroySampler)
            {
                api.DestroySampler(device, resources.Sampler, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.Sampler,
                    resources.Sampler.Handle);
                Descriptors.UnregisterLiveSampler(resources.Sampler);
                destroyedSamplers++;
            }

            if (TryTakeImageViewGeneration(
                    resources.PrimaryView,
                    entry.PrimaryViewGeneration))
            {
                api.DestroyImageView(device, resources.PrimaryView, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.ImageView,
                    resources.PrimaryView.Handle);
                destroyedViews++;
            }

            if (resources.AttachmentViews is not null)
            {
                for (int viewIndex = 0;
                     viewIndex < resources.AttachmentViews.Length;
                     viewIndex++)
                {
                    ImageView view = resources.AttachmentViews[viewIndex];
                    ulong generation =
                        viewIndex < entry.AttachmentViewGenerations.Length
                            ? entry.AttachmentViewGenerations[viewIndex]
                            : 0;
                    if (!TryTakeImageViewGeneration(view, generation))
                        continue;

                    api.DestroyImageView(device, view, null);
                    CompleteSimpleResourceDestruction(
                        ObjectType.ImageView,
                        view.Handle);
                    destroyedViews++;
                }
            }

            if (canDestroyImage)
            {
                api.DestroyImage(device, resources.Image, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.Image,
                    resources.Image.Handle);
                Lifetime.ImageViews.RetiringImageHandles.TryRemove(
                    resources.Image.Handle,
                    out _);
                destroyedImages++;
                if (resources.AllocatedVRAMBytes > 0)
                    destroyedImageBytes += resources.AllocatedVRAMBytes;
            }

            if (canDestroyImage &&
                hasTrackedImageAllocation &&
                trackedImageAllocation.Memory.Handle != 0)
            {
                Allocations.Buffers.MemoryAllocator!.Free(
                    api,
                    device,
                    trackedImageAllocation);
                freedMemories++;
            }

            CompleteRetiredImageDeduplication(frameSlot, in entry);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            images: destroyedImages,
            imageViews: destroyedViews,
            samplers: destroyedSamplers,
            imageMemories: freedMemories,
            imageBytes: destroyedImageBytes);
    }

    private bool CanDestroyResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration)
    {
        if (handle == 0 || expectedGeneration == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            bool forced = Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            return Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    new VulkanResourceLifetimeKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource) &&
                resource.Generation == expectedGeneration &&
                (resource.State & EVulkanResourceLifetimeState.Destroyed) == 0 &&
                (forced ||
                 (Lifetime.Tracker.IsRetirementReadyNoLock(
                      resource.RetirementTicket) &&
                  resource.Pins.IsRetirementReady(
                      Lifetime.Tracker.CompletedGraphicsSequence,
                      Lifetime.Tracker.CompletedTransferSequence,
                      Lifetime.Tracker.CompletedOtherSequence)));
        }
    }

    private bool TryTakeImageViewGeneration(
        ImageView imageView,
        ulong expectedGeneration)
    {
        if (!CanDestroyResourceGeneration(
                ObjectType.ImageView,
                imageView.Handle,
                expectedGeneration) ||
            !Lifetime.ImageViews.LiveHandles.TryRemove(imageView.Handle, out _))
        {
            return false;
        }

        Lifetime.ImageViews.DescriptorHeapCreateInfos.TryRemove(
            imageView.Handle,
            out _);
        return true;
    }

    private bool HasUndestroyedImageDependency(
        in RetiredImageResources resources)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (resources.Image.Handle != 0)
            {
                foreach ((ulong viewHandle, ulong backingImageHandle) in
                         Lifetime.Tracker.ImageViewBackingImages)
                {
                    if (backingImageHandle != resources.Image.Handle ||
                        ContainsRetiredImageView(resources, viewHandle))
                    {
                        continue;
                    }

                    if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                            new VulkanResourceLifetimeKey(
                                ObjectType.ImageView,
                                viewHandle),
                            out VulkanResourceLifetimeRecord? view) ||
                        (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    {
                        return true;
                    }
                }
            }

            foreach ((ulong framebufferHandle, VulkanResourceLifetimeKey[] attachments)
                     in Lifetime.Tracker.FramebufferAttachments)
            {
                if (Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(
                            ObjectType.Framebuffer,
                            framebufferHandle),
                        out VulkanResourceLifetimeRecord? framebuffer) &&
                    (framebuffer.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                {
                    continue;
                }

                for (int index = 0; index < attachments.Length; index++)
                {
                    VulkanResourceLifetimeKey attachment = attachments[index];
                    if (ContainsRetiredImageView(resources, attachment.Handle) ||
                        (resources.Image.Handle != 0 &&
                         Lifetime.Tracker.ImageViewBackingImages.TryGetValue(
                             attachment.Handle,
                             out ulong backingImageHandle) &&
                         backingImageHandle == resources.Image.Handle))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsRetiredImageView(
        in RetiredImageResources resources,
        ulong viewHandle)
    {
        if (viewHandle == 0)
            return false;
        if (resources.PrimaryView.Handle == viewHandle)
            return true;

        ImageView[]? attachmentViews = resources.AttachmentViews;
        if (attachmentViews is null)
            return false;
        for (int index = 0; index < attachmentViews.Length; index++)
            if (attachmentViews[index].Handle == viewHandle)
                return true;

        return false;
    }

    private void CompleteRetiredImageDeduplication(
        int frameSlot,
        in RetiredImageResourceEntry entry)
    {
        RetiredImageResources resources = entry.Resources;
        lock (Lifetime.Retirement.SyncRoot)
        {
            if (resources.Image.Handle != 0)
            {
                Lifetime.Retirement.ImageHandles[frameSlot].Remove(
                    resources.Image.Handle);
                Lifetime.Retirement.AllImageHandles.Remove(
                    resources.Image.Handle);
            }
            if (resources.Memory.Handle != 0)
            {
                Lifetime.Retirement.ImageMemoryHandles[frameSlot].Remove(
                    resources.Memory.Handle);
                Lifetime.Retirement.AllImageMemoryHandles.Remove(
                    resources.Memory.Handle);
            }
            RemoveRetiredImageViewDeduplication(
                frameSlot,
                resources.PrimaryView,
                entry.PrimaryViewGeneration);
            if (resources.AttachmentViews is not null)
            {
                for (int index = 0;
                     index < resources.AttachmentViews.Length;
                     index++)
                {
                    ulong generation =
                        index < entry.AttachmentViewGenerations.Length
                            ? entry.AttachmentViewGenerations[index]
                            : 0;
                    RemoveRetiredImageViewDeduplication(
                        frameSlot,
                        resources.AttachmentViews[index],
                        generation);
                }
            }
            if (resources.Sampler.Handle != 0)
            {
                Lifetime.Retirement.SamplerHandles[frameSlot].Remove(
                    resources.Sampler.Handle);
                Lifetime.Retirement.AllSamplerHandles.Remove(
                    resources.Sampler.Handle);
            }
        }
    }

    private void RemoveRetiredImageViewDeduplication(
        int frameSlot,
        ImageView view,
        ulong generation)
    {
        if (view.Handle == 0)
            return;

        VulkanPinnedResourceGeneration key = new(
            new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle),
            generation);
        Lifetime.Retirement.ImageViewHandles[frameSlot].Remove(key);
        Lifetime.Retirement.AllImageViewHandles.Remove(key);
    }

    private bool HasUndestroyedBufferView(Silk.NET.Vulkan.Buffer buffer)
    {
        if (buffer.Handle == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            foreach ((ulong viewHandle, ulong backingBufferHandle) in
                     Lifetime.Tracker.BufferViewBackingBuffers)
            {
                if (backingBufferHandle != buffer.Handle)
                    continue;

                if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(ObjectType.BufferView, viewHandle),
                        out VulkanResourceLifetimeRecord? view) ||
                    (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryTakeLiveBuffer(Silk.NET.Vulkan.Buffer buffer)
        => Allocations.Buffers.LiveHandles.TryRemove(buffer.Handle, out _);

    private void ReactivateResourceAfterRetirement(
        ObjectType type,
        ulong handle,
        string owner)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(
                    new VulkanResourceLifetimeKey(type, handle),
                    owner);
            if (!Lifetime.Tracker.IsRetirementReadyNoLock(
                    resource.RetirementTicket))
            {
                throw new InvalidOperationException(
                    $"Cannot recycle {resource.Key} before its retirement completion point is reached.");
            }

            resource.Owner = owner;
            resource.State = EVulkanResourceLifetimeState.CpuOwned;
            resource.Pins.ResetCompletion();
            resource.RetirementSerial = 0;
            resource.RetirementTicket = default;
        }
    }

    internal unsafe void DrainRetiredDescriptorSets(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredDescriptorSet> list = Lifetime.Retirement.DescriptorSets[frameSlot];
        List<RetiredDescriptorSet> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredDescriptorSet candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorSet.Handle,
                    Lifetime.Retirement.DescriptorSetHandles,
                    Lifetime.Retirement.AllDescriptorSetHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            RetiredDescriptorSet entry = ready[index];
            DescriptorSet descriptorSet = entry.DescriptorSet;
            Result result = api.FreeDescriptorSets(
                device,
                entry.DescriptorPool,
                1,
                &descriptorSet);
            if (result != Result.Success)
            {
                lock (Lifetime.Retirement.SyncRoot)
                {
                    VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                        frameSlot,
                        entry.DescriptorSet.Handle,
                        entry,
                        Lifetime.Retirement.DescriptorSets,
                        Lifetime.Retirement.DescriptorSetHandles,
                        Lifetime.Retirement.AllDescriptorSetHandles);
                }
                continue;
            }

            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorSet,
                entry.DescriptorSet.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            descriptorSets: destroyed);
    }

    internal unsafe void DrainRetiredDescriptorPools(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<RetiredDescriptorPool> list = Lifetime.Retirement.DescriptorPools[frameSlot];
        List<RetiredDescriptorPool> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredDescriptorPool candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorPool.Handle,
                    Lifetime.Retirement.DescriptorPoolHandles,
                    Lifetime.Retirement.AllDescriptorPoolHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            DescriptorPool pool = ready[index].DescriptorPool;
            if (pool.Handle == 0)
                continue;

            api.DestroyDescriptorPool(device, pool, null);
            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorPool,
                pool.Handle);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            descriptorPools: destroyed);
    }

    private void CompleteSimpleResourceDestruction(
        ObjectType type,
        ulong handle)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            if (!Lifetime.Tracker.IsRetirementReadyNoLock(
                    resource.RetirementTicket) ||
                !resource.Pins.IsRetirementReady(
                    Lifetime.Tracker.CompletedGraphicsSequence,
                    Lifetime.Tracker.CompletedTransferSequence,
                    Lifetime.Tracker.CompletedOtherSequence))
            {
                throw new InvalidOperationException(
                    $"Attempted to destroy {key} generation {resource.Generation} before its GPU completion point was reached.");
            }

            resource.State = EVulkanResourceLifetimeState.Destroyed;
            Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
            if (type == ObjectType.DescriptorSet)
                RemoveDescriptorSetLifetimeNoLock(handle);
            if (type == ObjectType.DescriptorPool)
                RemoveDescriptorSetsOwnedByPoolNoLock(handle);
            if (type == ObjectType.CommandBuffer &&
                Lifetime.Tracker.CommandBufferLifetimes.Remove(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? commandBufferLifetime))
            {
                ReleaseCommandBufferDependenciesNoLock(
                    handle,
                    commandBufferLifetime);
                VulkanResourceLifetimeKey poolKey =
                    commandBufferLifetime.AllocatingCommandPool;
                if (poolKey.IsValid &&
                    Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                        poolKey,
                        out HashSet<ulong>? children))
                {
                    children.Remove(handle);
                    if (children.Count == 0)
                        Lifetime.Tracker.CommandBuffersByPool.Remove(poolKey);
                }
            }
            if (type == ObjectType.CommandPool)
                Lifetime.Tracker.CommandBuffersByPool.Remove(key);
            if (type == ObjectType.ImageView)
                Lifetime.Tracker.ImageViewBackingImages.Remove(handle);
            if (type == ObjectType.BufferView)
                Lifetime.Tracker.BufferViewBackingBuffers.Remove(handle);
            if (type == ObjectType.Framebuffer)
                Lifetime.Tracker.FramebufferAttachments.Remove(handle);
        }
    }

    private void RemoveDescriptorSetsOwnedByPoolNoLock(ulong poolHandle)
    {
        if (!Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(
                poolHandle,
                out HashSet<ulong>? ownedSets))
        {
            return;
        }

        ulong[] removedSets = [.. ownedSets];
        for (int index = 0; index < removedSets.Length; index++)
            RemoveDescriptorSetLifetimeNoLock(removedSets[index]);

        Lifetime.Tracker.DescriptorSetsByPool.Remove(poolHandle);
    }

    private void RemoveDescriptorSetLifetimeNoLock(ulong setHandle)
    {
        if (Lifetime.Tracker.DescriptorSetLifetimes.Remove(
                setHandle,
                out VulkanDescriptorSetLifetimeRecord? state))
        {
            foreach ((VulkanResourceLifetimeKey key, ulong generation) in
                     state.PinnedReferences)
            {
                if (Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        key,
                        out VulkanResourceLifetimeRecord? resource) &&
                    resource.Generation == generation)
                {
                    resource.Pins.ReleaseDescriptorReference();
                }
            }

            state.PinnedReferences.Clear();
            if (state.Pool.Handle != 0 &&
                Lifetime.Tracker.DescriptorSetsByPool.TryGetValue(
                    state.Pool.Handle,
                    out HashSet<ulong>? poolSets))
            {
                poolSets.Remove(setHandle);
                if (poolSets.Count == 0)
                    Lifetime.Tracker.DescriptorSetsByPool.Remove(state.Pool.Handle);
            }

            foreach (VulkanResourceLifetimeKey reference in
                     state.IndexedReferences)
            {
                if (!Lifetime.Tracker.DescriptorSetsByReferencedResource.TryGetValue(
                        reference,
                        out HashSet<ulong>? sets))
                {
                    continue;
                }

                sets.Remove(setHandle);
                if (sets.Count == 0)
                    Lifetime.Tracker.DescriptorSetsByReferencedResource.Remove(reference);
            }
        }

        Lifetime.Tracker.PublishedDescriptorSets.TryRemove(setHandle, out _);
        if (Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, setHandle),
                out VulkanResourceLifetimeRecord? setResource))
        {
            setResource.State = EVulkanResourceLifetimeState.Destroyed;
        }
    }

    internal bool TryValidatePresentationSourceForReplay(
        in VulkanPresentationSourceTuple source,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!source.HasLogicalSource || source.LogicalEpoch == 0 ||
            source.Image.Handle == 0 || source.ImageView.Handle == 0 ||
            source.Sampler.Handle == 0 ||
            source.ExpectedLayout == ImageLayout.Undefined ||
            source.Width == 0 || source.Height == 0)
        {
            failureReason =
                $"final presentation source epoch {source.LogicalEpoch} is not replayable";
            return false;
        }

        ulong currentImageGeneration = GetPublishedGeneration(
            ObjectType.Image,
            source.Image.Handle);
        ulong currentImageViewGeneration = GetPublishedGeneration(
            ObjectType.ImageView,
            source.ImageView.Handle);
        ulong currentSamplerGeneration = GetPublishedGeneration(
            ObjectType.Sampler,
            source.Sampler.Handle);
        if (currentImageGeneration == source.ImageAllocationGeneration &&
            currentImageViewGeneration == source.ImageViewGeneration &&
            currentSamplerGeneration == source.SamplerGeneration)
        {
            return true;
        }

        failureReason =
            $"final presentation replay source epoch {source.LogicalEpoch} references a superseded native image generation";
        return false;
    }

    /// <summary>
    /// Mapped frame storage is created only after device and frame-slot setup. Replacing it is
    /// intentionally explicit so an old generation cannot be silently retargeted.
    /// </summary>
    internal VulkanMappedFrameArena? MappedFrameArena { get; private set; }

    internal void PublishMappedFrameArena(VulkanMappedFrameArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (MappedFrameArena is not null)
            throw new InvalidOperationException("A mapped frame arena is already published.");

        MappedFrameArena = arena;
    }

    internal VulkanMappedFrameArena? DetachMappedFrameArena()
    {
        VulkanMappedFrameArena? arena = MappedFrameArena;
        MappedFrameArena = null;
        return arena;
    }
}
