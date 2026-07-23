using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Resource Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for buffer handles that cannot be destroyed
        /// immediately because a command buffer recorded during the same frame may still
        /// reference them.  Drained after <c>WaitForFences</c> signals that the slot's
        /// GPU work has completed.
        /// </summary>
        private readonly record struct RetiredBuffer(
            Silk.NET.Vulkan.Buffer Buffer,
            DeviceMemory Memory,
            VulkanRetirementTicket Ticket);

        private readonly List<RetiredBuffer>[] _retiredBuffers =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        /// <summary>
        /// Tracks queued retired handles per frame slot to prevent duplicate
        /// enqueue/destruction of the same Vulkan handle.
        /// </summary>
        private readonly HashSet<ulong>[] _retiredBufferHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredBufferHandlesAll = new();

        private readonly HashSet<ulong>[] _retiredMemoryHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredMemoryHandlesAll = new();

        private readonly object _retiredResourceLock = new();
        private const int RetiredDescriptorPoolDrainLimitPerFrame = 8;
        private const int RetiredDescriptorSetDrainLimitPerFrame = 64;
        private const int RetiredPipelineDrainLimitPerFrame = 8;
        private const int RetiredQueryPoolDrainLimitPerFrame = 32;
        private const int RetiredCommandBufferDrainLimitPerFrame = 128;
        private const int RetiredBufferViewDrainLimitPerFrame = 64;
        private const int RetiredFramebufferDrainLimitPerFrame = 64;
        private const int RetiredBufferDrainLimitPerFrame = 256;
        private const int RetiredImageDrainLimitPerFrame = 64;

        private static int GetRetiredResourceDrainCount(int queuedCount, int maxItems)
            => queuedCount <= 0 || maxItems <= 0 ? 0 : queuedCount <= maxItems ? queuedCount : maxItems;

        private void ReportRetiredResourceBacklog(string resourceKind, int frameSlot, int remaining)
        {
            if (remaining <= 0)
                return;

            Debug.VulkanEvery(
                $"Vulkan.RetiredResourceBacklog.{GetHashCode()}.{resourceKind}.{frameSlot}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Retired {0} backlog remains for frame slot {1}: {2}",
                resourceKind,
                frameSlot,
                remaining);
        }

        // =========== Framebuffer Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for framebuffer handles that cannot be
        /// destroyed immediately because an in-flight command buffer may still
        /// reference them.  Drained alongside buffers and images.
        /// </summary>
        private readonly record struct RetiredFramebuffer(
            Framebuffer Framebuffer,
            VulkanRetirementTicket Ticket);

        private readonly List<RetiredFramebuffer>[] _retiredFramebuffers =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredFramebufferHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredFramebufferHandlesAll = new();

        private readonly List<RetiredDescriptorPool>[] _retiredDescriptorPools =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredDescriptorPoolHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredDescriptorPoolHandlesAll = new();

        private readonly List<RetiredDescriptorSet>[] _retiredDescriptorSets =
            [new(), new()];

        private readonly HashSet<ulong>[] _retiredDescriptorSetHandles =
            [new(), new()];

        private readonly HashSet<ulong> _retiredDescriptorSetHandlesAll = new();

        private readonly List<RetiredPipeline>[] _retiredPipelines =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredPipelineHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredPipelineHandlesAll = new();

        private readonly List<RetiredQueryPool>[] _retiredQueryPools =
            [new(), new()];

        private readonly HashSet<ulong>[] _retiredQueryPoolHandles =
            [new(), new()];

        private readonly HashSet<ulong> _retiredQueryPoolHandlesAll = new();

        private readonly List<RetiredCommandBuffer>[] _retiredCommandBuffers =
            [new(), new()];

        private readonly HashSet<ulong>[] _retiredCommandBufferHandles =
            [new(), new()];

        private readonly HashSet<ulong> _retiredCommandBufferHandlesAll = new();

        private readonly List<RetiredBufferView>[] _retiredBufferViews =
            [new(), new()];

        private readonly HashSet<ulong>[] _retiredBufferViewHandles =
            [new(), new()];

        private readonly HashSet<ulong> _retiredBufferViewHandlesAll = new();

        internal void RetirePipeline(Pipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.Pipeline,
                pipeline.Handle,
                nameof(RetirePipeline));
            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (!_retiredPipelineHandlesAll.Add(pipeline.Handle))
                    return;

                _retiredPipelineHandles[frameSlot].Add(pipeline.Handle);
                _retiredPipelines[frameSlot].Add(new RetiredPipeline(pipeline, ticket));
            }
        }

        private void DrainRetiredPipelines(int maxItems = RetiredPipelineDrainLimitPerFrame)
        {
            DrainRetiredPipelines(currentFrame, maxItems);
        }

        private void DrainRetiredPipelines(int frameSlot, int maxItems)
        {
            RetiredPipeline[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                List<RetiredPipeline> list = _retiredPipelines[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredPipeline> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredPipeline candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    if (candidate.Pipeline.Handle != 0)
                    {
                        _retiredPipelineHandles[frameSlot].Remove(candidate.Pipeline.Handle);
                        _retiredPipelineHandlesAll.Remove(candidate.Pipeline.Handle);
                    }
                }
                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("pipelines", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedPipelines = 0;
            foreach (RetiredPipeline entry in retired)
            {
                Pipeline pipeline = entry.Pipeline;
                if (pipeline.Handle != 0)
                {
                    Api!.DestroyPipeline(device, pipeline, null);
                    CompleteVulkanResourceDestruction(ObjectType.Pipeline, pipeline.Handle);
                    destroyedPipelines++;
                }
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(pipelines: destroyedPipelines);
        }

        internal void RetireDescriptorPool(DescriptorPool descriptorPool)
        {
            if (descriptorPool.Handle == 0)
                return;

            int frameSlot = currentFrame;
            lock (_retiredResourceLock)
            {
                if (!_retiredDescriptorPoolHandlesAll.Add(descriptorPool.Handle))
                    return;
            }

            VulkanRetirementTicket ticket;
            try
            {
                ticket = CaptureVulkanDescriptorPoolRetirementTicket(
                    descriptorPool,
                    nameof(RetireDescriptorPool));
            }
            catch
            {
                lock (_retiredResourceLock)
                    _retiredDescriptorPoolHandlesAll.Remove(descriptorPool.Handle);
                throw;
            }

            lock (_retiredResourceLock)
            {
                RemoveRetiredDescriptorSetsForPool_NoLock(descriptorPool.Handle);
                _retiredDescriptorPoolHandles[frameSlot].Add(descriptorPool.Handle);
                _retiredDescriptorPools[frameSlot].Add(new RetiredDescriptorPool(descriptorPool, ticket));
            }
        }

        private void RemoveRetiredDescriptorSetsForPool_NoLock(ulong descriptorPoolHandle)
        {
            for (int frameSlot = 0; frameSlot < _retiredDescriptorSets.Length; frameSlot++)
            {
                List<RetiredDescriptorSet> sets = _retiredDescriptorSets[frameSlot];
                for (int i = sets.Count - 1; i >= 0; i--)
                {
                    RetiredDescriptorSet entry = sets[i];
                    if (entry.DescriptorPool.Handle != descriptorPoolHandle)
                        continue;

                    sets.RemoveAt(i);
                    _retiredDescriptorSetHandles[frameSlot].Remove(entry.DescriptorSet.Handle);
                    _retiredDescriptorSetHandlesAll.Remove(entry.DescriptorSet.Handle);
                }
            }
        }

        private void DrainRetiredDescriptorPools()
            => DrainRetiredDescriptorPools(currentFrame, RetiredDescriptorPoolDrainLimitPerFrame);

        private void DrainRetiredDescriptorPools(int frameSlot, int maxItems = int.MaxValue)
        {
            RetiredDescriptorPool[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                List<RetiredDescriptorPool> list = _retiredDescriptorPools[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredDescriptorPool> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredDescriptorPool candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    if (candidate.DescriptorPool.Handle != 0)
                    {
                        _retiredDescriptorPoolHandles[frameSlot].Remove(candidate.DescriptorPool.Handle);
                        _retiredDescriptorPoolHandlesAll.Remove(candidate.DescriptorPool.Handle);
                    }
                }
                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("descriptor pools", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedPools = 0;
            foreach (RetiredDescriptorPool entry in retired)
            {
                DescriptorPool pool = entry.DescriptorPool;
                if (pool.Handle == 0)
                    continue;

                Api!.DestroyDescriptorPool(device, pool, null);
                CompleteVulkanResourceDestruction(ObjectType.DescriptorPool, pool.Handle);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                destroyedPools++;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(descriptorPools: destroyedPools);
        }

        internal void DrainAllRetiredDescriptorPools()
        {
            for (int frameSlot = 0; frameSlot < _retiredDescriptorPools.Length; frameSlot++)
            {
                DrainRetiredDescriptorSets(frameSlot, int.MaxValue);
                DrainRetiredDescriptorPools(frameSlot, int.MaxValue);
            }
        }

        internal void RetireDescriptorSet(DescriptorPool descriptorPool, DescriptorSet descriptorSet)
        {
            if (descriptorPool.Handle == 0 || descriptorSet.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.DescriptorSet,
                descriptorSet.Handle,
                nameof(RetireDescriptorSet));
            int frameSlot = currentFrame;
            lock (_retiredResourceLock)
            {
                if (_retiredDescriptorPoolHandlesAll.Contains(descriptorPool.Handle))
                    return;

                if (!_retiredDescriptorSetHandlesAll.Add(descriptorSet.Handle))
                    return;

                _retiredDescriptorSetHandles[frameSlot].Add(descriptorSet.Handle);
                _retiredDescriptorSets[frameSlot].Add(new RetiredDescriptorSet(
                    descriptorPool,
                    descriptorSet,
                    ticket));
            }
        }

        private void DrainRetiredDescriptorSets(
            int frameSlot,
            int maxItems = RetiredDescriptorSetDrainLimitPerFrame)
        {
            RetiredDescriptorSet[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<RetiredDescriptorSet> list = _retiredDescriptorSets[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredDescriptorSet> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredDescriptorSet candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    _retiredDescriptorSetHandles[frameSlot].Remove(candidate.DescriptorSet.Handle);
                    _retiredDescriptorSetHandlesAll.Remove(candidate.DescriptorSet.Handle);
                }

                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("descriptor sets", frameSlot, remaining);
            int destroyedDescriptorSets = 0;
            for (int i = 0; i < retired.Length; i++)
            {
                RetiredDescriptorSet entry = retired[i];
                DescriptorSet descriptorSet = entry.DescriptorSet;
                Result result = Api!.FreeDescriptorSets(device, entry.DescriptorPool, 1, &descriptorSet);
                if (result != Result.Success)
                {
                    Debug.VulkanWarning(
                        "[Vulkan.ResourceLifetime] Failed to free retired descriptor set 0x{0:X}. Result={1}.",
                        entry.DescriptorSet.Handle,
                        result);
                    RetireDescriptorSet(entry.DescriptorPool, entry.DescriptorSet);
                    continue;
                }

                CompleteVulkanResourceDestruction(ObjectType.DescriptorSet, entry.DescriptorSet.Handle);
                destroyedDescriptorSets++;
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                descriptorSets: destroyedDescriptorSets);
        }

        internal void RetireQueryPool(QueryPool queryPool)
        {
            if (queryPool.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.QueryPool,
                queryPool.Handle,
                nameof(RetireQueryPool));
            int frameSlot = currentFrame;
            lock (_retiredResourceLock)
            {
                if (!_retiredQueryPoolHandlesAll.Add(queryPool.Handle))
                    return;

                _retiredQueryPoolHandles[frameSlot].Add(queryPool.Handle);
                _retiredQueryPools[frameSlot].Add(new RetiredQueryPool(queryPool, ticket));
            }
        }

        private void DrainRetiredQueryPools(
            int frameSlot,
            int maxItems = RetiredQueryPoolDrainLimitPerFrame)
        {
            RetiredQueryPool[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<RetiredQueryPool> list = _retiredQueryPools[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredQueryPool> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredQueryPool candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    _retiredQueryPoolHandles[frameSlot].Remove(candidate.QueryPool.Handle);
                    _retiredQueryPoolHandlesAll.Remove(candidate.QueryPool.Handle);
                }

                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("query pools", frameSlot, remaining);
            for (int i = 0; i < retired.Length; i++)
            {
                QueryPool queryPool = retired[i].QueryPool;
                Api!.DestroyQueryPool(device, queryPool, null);
                CompleteVulkanResourceDestruction(ObjectType.QueryPool, queryPool.Handle);
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                queryPools: retired.Length);
        }

        internal void RetireCommandBuffer(CommandPool commandPool, CommandBuffer commandBuffer)
        {
            if (commandPool.Handle == 0 || commandBuffer.Handle == 0)
                return;

            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.CommandBuffer,
                commandBufferHandle,
                nameof(RetireCommandBuffer));
            int frameSlot = currentFrame;
            lock (_retiredResourceLock)
            {
                if (!_retiredCommandBufferHandlesAll.Add(commandBufferHandle))
                    return;

                _retiredCommandBufferHandles[frameSlot].Add(commandBufferHandle);
                _retiredCommandBuffers[frameSlot].Add(new RetiredCommandBuffer(
                    commandPool,
                    commandBuffer,
                    ticket));
            }
        }

        private bool IsCommandBufferPendingRetirement(CommandBuffer commandBuffer)
        {
            if (commandBuffer.Handle == 0)
                return false;

            ulong handle = unchecked((ulong)commandBuffer.Handle);
            lock (_retiredResourceLock)
                return _retiredCommandBufferHandlesAll.Contains(handle);
        }

        private void DrainRetiredCommandBuffers(
            int frameSlot,
            int maxItems = RetiredCommandBufferDrainLimitPerFrame)
        {
            RetiredCommandBuffer[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<RetiredCommandBuffer> list = _retiredCommandBuffers[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredCommandBuffer> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredCommandBuffer candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    _retiredCommandBufferHandles[frameSlot].Remove(
                        unchecked((ulong)candidate.CommandBuffer.Handle));
                    _retiredCommandBufferHandlesAll.Remove(
                        unchecked((ulong)candidate.CommandBuffer.Handle));
                }

                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("command buffers", frameSlot, remaining);
            for (int i = 0; i < retired.Length; i++)
            {
                RetiredCommandBuffer entry = retired[i];
                CommandBuffer commandBuffer = entry.CommandBuffer;
                Api!.FreeCommandBuffers(device, entry.CommandPool, 1, &commandBuffer);
                RemoveCommandBufferBindState(entry.CommandBuffer);
                UntrackOwnedCommandChainSecondaryCommandBuffer(entry.CommandPool, entry.CommandBuffer);
                DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(entry.CommandPool);
                CompleteVulkanResourceDestruction(
                    ObjectType.CommandBuffer,
                    unchecked((ulong)entry.CommandBuffer.Handle));
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                commandBuffers: retired.Length);
        }

        internal void RetireBufferView(BufferView bufferView)
        {
            if (bufferView.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.BufferView,
                bufferView.Handle,
                nameof(RetireBufferView));
            int frameSlot = currentFrame;
            lock (_retiredResourceLock)
            {
                if (!_retiredBufferViewHandlesAll.Add(bufferView.Handle))
                    return;

                _retiredBufferViewHandles[frameSlot].Add(bufferView.Handle);
                _retiredBufferViews[frameSlot].Add(new RetiredBufferView(bufferView, ticket));
            }
        }

        private void DrainRetiredBufferViews(
            int frameSlot,
            int maxItems = RetiredBufferViewDrainLimitPerFrame)
        {
            RetiredBufferView[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<RetiredBufferView> list = _retiredBufferViews[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredBufferView> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredBufferView candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    _retiredBufferViewHandles[frameSlot].Remove(candidate.BufferView.Handle);
                    _retiredBufferViewHandlesAll.Remove(candidate.BufferView.Handle);
                }

                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("buffer views", frameSlot, remaining);
            for (int i = 0; i < retired.Length; i++)
            {
                BufferView bufferView = retired[i].BufferView;
                Api!.DestroyBufferView(device, bufferView, null);
                CompleteVulkanResourceDestruction(ObjectType.BufferView, bufferView.Handle);
            }
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                bufferViews: retired.Length);
        }

        internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction(string reason)
        {
            int meshRendererCount = 0;
            int materialCount = 0;
            int frameBufferCount = 0;
            int computeCachedPoolCount = ReleaseComputeDescriptorReferencesForPhysicalResourceDestruction();
            int computeTransientPoolCount = ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction();
            int trackedImageLayoutCount = ClearAllTrackedImageLayouts();

            foreach (var apiObject in RenderObjectCache.Values.ToArray())
            {
                switch (apiObject)
                {
                    case VkMeshRenderer meshRenderer:
                        meshRenderer.ReleaseDescriptorReferencesForPhysicalResourceDestruction();
                        meshRendererCount++;
                        break;
                    case VkMaterial material:
                        material.ReleaseDescriptorReferencesForPhysicalResourceDestruction();
                        materialCount++;
                        break;
                    case VkFrameBuffer frameBuffer:
                        if (frameBuffer.InvalidateCachedAttachmentState())
                            frameBufferCount++;
                        break;
                }
            }

            int commandChainSecondaryCount = InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();
            MarkOpenXrPrimaryCommandBufferVariantsDirty();
            MarkCommandBuffersDirty();

            Debug.VulkanEvery(
                $"Vulkan.ResourceDestroy.ReleaseDescriptorReferences.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Released descriptor references before physical resource destruction: reason={0} meshRenderers={1} materials={2} frameBuffers={3} computeCachedPools={4} computeTransientPools={5} commandChainSecondaries={6} trackedImageLayouts={7}.",
                reason,
                meshRendererCount,
                materialCount,
                frameBufferCount,
                computeCachedPoolCount,
                computeTransientPoolCount,
                commandChainSecondaryCount,
                trackedImageLayoutCount);
        }

        /// <summary>
        /// Queues a VkFramebuffer for deferred destruction.  The handle will be
        /// destroyed the next time this frame slot is reused (after the timeline
        /// wait guarantees the GPU is done with it).
        /// </summary>
        internal void RetireFramebuffer(Framebuffer framebuffer)
        {
            if (framebuffer.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.Framebuffer,
                framebuffer.Handle,
                nameof(RetireFramebuffer));
            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (!_retiredFramebufferHandlesAll.Add(framebuffer.Handle))
                    return;

                _retiredFramebufferHandles[frameSlot].Add(framebuffer.Handle);
                _retiredFramebuffers[frameSlot].Add(new RetiredFramebuffer(framebuffer, ticket));
            }
        }

        /// <summary>
        /// Destroys all framebuffers that were retired during the last use of
        /// the current frame slot.  Called immediately after <c>WaitForFences</c>.
        /// </summary>
        private void DrainRetiredFramebuffers(int maxItems = RetiredFramebufferDrainLimitPerFrame)
        {
            DrainRetiredFramebuffers(currentFrame, maxItems);
        }

        private void DrainRetiredFramebuffers(int frameSlot, int maxItems)
        {
            RetiredFramebuffer[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<RetiredFramebuffer> list = _retiredFramebuffers[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredFramebuffer> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredFramebuffer candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    if (candidate.Framebuffer.Handle != 0)
                    {
                        _retiredFramebufferHandles[frameSlot].Remove(candidate.Framebuffer.Handle);
                        _retiredFramebufferHandlesAll.Remove(candidate.Framebuffer.Handle);
                    }
                }
                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("framebuffers", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedFramebuffers = 0;
            foreach (RetiredFramebuffer entry in retired)
            {
                Framebuffer fb = entry.Framebuffer;
                if (fb.Handle != 0)
                {
                    Api!.DestroyFramebuffer(device, fb, null);
                    CompleteVulkanResourceDestruction(ObjectType.Framebuffer, fb.Handle);
                    destroyedFramebuffers++;
                }
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(framebuffers: destroyedFramebuffers);
        }

        /// <summary>
        /// Queues a buffer+memory pair for deferred destruction.  The pair will be
        /// destroyed the next time this frame slot is reused (after the fence wait
        /// guarantees the GPU is done with it).
        /// </summary>
        internal void RetireBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0 && memory.Handle == 0)
                return;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.Buffer,
                buffer.Handle,
                nameof(RetireBuffer));
            if (buffer.Handle == 0 && memory.Handle != 0)
                ticket = ticket.Merge(CaptureVulkanRetirementWatermark());
            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (buffer.Handle != 0 && !_retiredBufferHandlesAll.Add(buffer.Handle))
                    return;

                if (buffer.Handle != 0 && !_retiredBufferHandles[frameSlot].Add(buffer.Handle))
                    buffer = default;

                if (memory.Handle != 0)
                {
                    if (!_retiredMemoryHandlesAll.Add(memory.Handle))
                        memory = default;
                    else
                        _retiredMemoryHandles[frameSlot].Add(memory.Handle);
                }

                if (buffer.Handle == 0 && memory.Handle == 0)
                    return;

                _retiredBuffers[frameSlot].Add(new RetiredBuffer(buffer, memory, ticket));
            }
        }

        /// <summary>
        /// Destroys all buffers that were retired during the last use of the current
        /// frame slot.  Called immediately after <c>WaitForFences</c>.
        /// </summary>
        private void DrainRetiredBuffers(int maxItems = RetiredBufferDrainLimitPerFrame)
        {
            DrainRetiredBuffers(currentFrame, maxItems);
        }

        private void DrainRetiredBuffers(int frameSlot, int maxItems)
        {
            RetiredBuffer[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                List<RetiredBuffer> list = _retiredBuffers[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredBuffer> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredBuffer candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket) ||
                        HasUndestroyedVulkanBufferViewReference(candidate.Buffer))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                    Silk.NET.Vulkan.Buffer buffer = candidate.Buffer;
                    DeviceMemory memory = candidate.Memory;
                    if (buffer.Handle != 0)
                    {
                        _retiredBufferHandles[frameSlot].Remove(buffer.Handle);
                        _retiredBufferHandlesAll.Remove(buffer.Handle);
                    }
                    if (memory.Handle != 0)
                    {
                        _retiredMemoryHandles[frameSlot].Remove(memory.Handle);
                        _retiredMemoryHandlesAll.Remove(memory.Handle);
                    }
                }
                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("buffers", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedBuffers = 0;
            int freedMemories = 0;
            int pooledBuffers = 0;
            foreach (RetiredBuffer entry in retired)
            {
                Silk.NET.Vulkan.Buffer buf = entry.Buffer;
                DeviceMemory mem = entry.Memory;
                DeviceMemory memory = mem;

                // Prefer tracked ownership so allocator-backed memory is never raw-freed.
                if (buf.Handle != 0)
                {
                    if (memory.Handle != 0 && _stagingManager.TryRelease(buf, memory))
                    {
                        ReactivateVulkanResourceAfterRetirement(ObjectType.Buffer, buf.Handle, "StagingPool.Reuse");
                        pooledBuffers++;
                        continue;
                    }

                    if (TryDestroyKnownBufferAllocation(buf, out bool destroyedTrackedBuffer, out bool freedTrackedMemory))
                    {
                        if (destroyedTrackedBuffer)
                            destroyedBuffers++;
                        if (freedTrackedMemory)
                            freedMemories++;
                        CompleteVulkanResourceDestruction(ObjectType.Buffer, buf.Handle);
                        continue;
                    }

                    if (TryBeginDestroyBuffer(buf, "DrainRetiredBuffers.Untracked"))
                    {
                        Api!.DestroyBuffer(device, buf, null);
                        CompleteVulkanResourceDestruction(ObjectType.Buffer, buf.Handle);
                        destroyedBuffers++;
                    }
                }

                if (memory.Handle != 0 && FreeUntrackedBufferMemory(memory, "DrainRetiredBuffers"))
                {
                    freedMemories++;
                }
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                buffers: destroyedBuffers,
                bufferMemories: freedMemories);

            if (pooledBuffers > 0)
            {
                _stagingManager.Trim(this);
                Debug.VulkanEvery(
                    $"Vulkan.TextureUpload.StagingReturnedToPool.{GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Returned {0} retired staging buffer(s) to the Vulkan staging pool for reuse.",
                    pooledBuffers);
            }
        }

        /// <summary>
        /// Per-frame-slot retirement queue for image resources that cannot be
        /// destroyed immediately because an in-flight command buffer may still
        /// reference them.  Drained alongside <see cref="_retiredBuffers"/>.
        /// </summary>
        private readonly List<RetiredImageResourceEntry>[] _retiredImages =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredImageHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredImageMemoryHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<VulkanPinnedResourceGeneration>[] _retiredImageViewHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredSamplerHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong> _retiredImageHandlesAll = new();
        private readonly HashSet<ulong> _retiredImageMemoryHandlesAll = new();
        private readonly HashSet<VulkanPinnedResourceGeneration> _retiredImageViewHandlesAll = new();
        private readonly HashSet<ulong> _retiredSamplerHandlesAll = new();

        /// <summary>
        /// Queues image resources for deferred destruction.  The resources will be
        /// destroyed the next time this frame slot is reused (after the timeline
        /// wait guarantees the GPU is done with them).
        /// </summary>
        internal void RetireImageResources(
            in RetiredImageResources resources,
            [CallerMemberName] string owner = "")
        {
            if (!CanQueueOwnedImageRetirement(resources.Image, resources.Memory, owner))
                return;

            ImageView primaryViewCandidate = CanQueueImageViewRetirement(
                resources.PrimaryView,
                owner)
                ? resources.PrimaryView
                : default;
            ImageView[] sourceAttachmentViews = FilterOwnedImageViewRetirementCandidates(
                resources.AttachmentViews,
                owner);
            VulkanRetirementTicket imageTicket = CaptureVulkanRetirementTicket(
                ObjectType.Image,
                resources.Image.Handle,
                owner);
            VulkanRetirementTicket ticket = imageTicket;
            if (resources.Image.Handle == 0 && resources.Memory.Handle != 0)
                ticket = ticket.Merge(CaptureVulkanRetirementWatermark());
            VulkanRetirementTicket primaryViewTicket = CaptureVulkanRetirementTicket(
                ObjectType.ImageView,
                primaryViewCandidate.Handle,
                owner);
            ticket = ticket.Merge(primaryViewTicket);
            ulong[] sourceAttachmentViewGenerations = sourceAttachmentViews.Length == 0
                ? []
                : new ulong[sourceAttachmentViews.Length];
            if (sourceAttachmentViews.Length != 0)
            {
                for (int i = 0; i < sourceAttachmentViews.Length; i++)
                {
                    VulkanRetirementTicket attachmentViewTicket = CaptureVulkanRetirementTicket(
                        ObjectType.ImageView,
                        sourceAttachmentViews[i].Handle,
                        owner);
                    sourceAttachmentViewGenerations[i] = attachmentViewTicket.ResourceGeneration;
                    ticket = ticket.Merge(attachmentViewTicket);
                }
            }
            VulkanRetirementTicket samplerTicket = CaptureVulkanRetirementTicket(
                ObjectType.Sampler,
                resources.Sampler.Handle,
                nameof(RetireImageResources));
            ticket = ticket.Merge(samplerTicket);
            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                Image image = resources.Image;
                DeviceMemory memory = resources.Memory;
                ImageView primaryView = primaryViewCandidate;
                Sampler sampler = resources.Sampler;

                if (image.Handle != 0)
                {
                    ClearTrackedImageLayouts(image);
                    _retiringImageHandles[image.Handle] = 0;
                }

                if (image.Handle != 0)
                {
                    if (!_retiredImageHandlesAll.Add(image.Handle))
                        image = default;
                    else
                        _retiredImageHandles[frameSlot].Add(image.Handle);
                }

                if (memory.Handle != 0)
                {
                    if (!_retiredImageMemoryHandlesAll.Add(memory.Handle))
                        memory = default;
                    else
                        _retiredImageMemoryHandles[frameSlot].Add(memory.Handle);
                }

                if (primaryView.Handle != 0)
                {
                    VulkanPinnedResourceGeneration primaryViewKey = new(
                        ResourceKey(ObjectType.ImageView, primaryView.Handle),
                        primaryViewTicket.ResourceGeneration);
                    if (!_retiredImageViewHandlesAll.Add(primaryViewKey))
                        primaryView = default;
                    else
                        _retiredImageViewHandles[frameSlot].Add(primaryViewKey);
                }

                ImageView[] attachmentViews = FilterRetiredAttachmentViews(
                    sourceAttachmentViews,
                    sourceAttachmentViewGenerations,
                    frameSlot,
                    out ulong[] attachmentViewGenerations);

                if (sampler.Handle != 0)
                {
                    if (!_retiredSamplerHandlesAll.Add(sampler.Handle))
                        sampler = default;
                    else
                    {
                        _retiredSamplerHandles[frameSlot].Add(sampler.Handle);
                    }
                }

                if (image.Handle == 0 &&
                    memory.Handle == 0 &&
                    primaryView.Handle == 0 &&
                    attachmentViews.Length == 0 &&
                    sampler.Handle == 0)
                {
                    return;
                }

                _retiredImages[frameSlot].Add(new RetiredImageResourceEntry(
                    new RetiredImageResources(
                        image,
                        memory,
                        primaryView,
                        attachmentViews,
                        sampler,
                        resources.AllocatedVRAMBytes),
                    ticket,
                    imageTicket.ResourceGeneration,
                    primaryViewTicket.ResourceGeneration,
                    attachmentViewGenerations,
                    samplerTicket.ResourceGeneration));
            }
        }

        private bool CanQueueOwnedImageRetirement(
            Image image,
            DeviceMemory memory,
            string owner)
        {
            if (image.Handle == 0)
                return true;

            VulkanResourceLifetimeKey key = ResourceKey(ObjectType.Image, image.Handle);
            lock (_vulkanResourceLifetimeLock)
            {
                if (_vulkanResourceLifetimes.TryGetValue(
                        key,
                        out VulkanResourceLifetimeRecord? lifetime) &&
                    (lifetime.State & EVulkanResourceLifetimeState.External) != 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Retirement.SkipExternalImage.{image.Handle}.{lifetime.Generation}.{owner}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan.ResourceLifetime] Rejected stale owned-image retirement because the numeric handle now belongs to an external generation. Resource={0} Generation={1} CurrentOwner={2} RequestedBy={3}.",
                        key,
                        lifetime.Generation,
                        lifetime.Owner,
                        owner);
                    return false;
                }
            }

            if (memory.Handle == 0 ||
                !_imageAllocations.TryGetValue(
                    image.Handle,
                    out VulkanMemoryAllocation currentAllocation) ||
                currentAllocation.Memory.Handle == memory.Handle)
            {
                return true;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.Retirement.SkipRecycledImageAllocation.{image.Handle}.{memory.Handle}.{owner}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Rejected stale image retirement because the numeric image handle now owns a different memory allocation. Image=0x{0:X} RequestedMemory=0x{1:X} CurrentMemory=0x{2:X} RequestedBy={3}.",
                image.Handle,
                memory.Handle,
                currentAllocation.Memory.Handle,
                owner);
            return false;
        }

        private bool CanQueueImageViewRetirement(ImageView view, string owner)
        {
            if (view.Handle == 0 ||
                !_liveImageViewHandles.TryGetValue(view.Handle, out string? currentOwner) ||
                !IsExternalImageViewOwner(currentOwner))
            {
                return true;
            }

            bool compatibleOwner =
                (currentOwner.StartsWith("Swapchain.Color", StringComparison.Ordinal) &&
                 owner.Contains("Swapchain", StringComparison.Ordinal)) ||
                (currentOwner.StartsWith("OpenXR.Swapchain", StringComparison.Ordinal) &&
                 owner.Contains("OpenXR", StringComparison.OrdinalIgnoreCase));
            if (compatibleOwner)
                return true;

            ulong generation = GetCurrentVulkanResourceGeneration(
                ObjectType.ImageView,
                view.Handle);
            Debug.VulkanWarningEvery(
                $"Vulkan.Retirement.SkipExternalImageView.{view.Handle}.{generation}.{owner}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Rejected stale image-view retirement because the numeric handle now belongs to an external generation. ImageView=0x{0:X} Generation={1} CurrentOwner={2} RequestedBy={3}.",
                view.Handle,
                generation,
                currentOwner,
                owner);
            return false;
        }

        private ImageView[] FilterOwnedImageViewRetirementCandidates(
            ImageView[]? views,
            string owner)
        {
            if (views is null || views.Length == 0)
                return [];

            List<ImageView>? filtered = null;
            for (int i = 0; i < views.Length; i++)
            {
                ImageView view = views[i];
                if (!CanQueueImageViewRetirement(view, owner))
                    continue;

                filtered ??= new List<ImageView>(views.Length);
                filtered.Add(view);
            }

            return filtered is null ? [] : [.. filtered];
        }

        private ImageView[] FilterRetiredAttachmentViews(
            ImageView[] views,
            ulong[] generations,
            int frameSlot,
            out ulong[] filteredGenerations)
        {
            if (views.Length == 0)
            {
                filteredGenerations = [];
                return [];
            }

            List<ImageView>? filtered = null;
            List<ulong>? filteredGenerationList = null;
            for (int i = 0; i < views.Length; i++)
            {
                ImageView view = views[i];
                ulong generation = i < generations.Length ? generations[i] : 0;
                VulkanPinnedResourceGeneration viewKey = new(
                    ResourceKey(ObjectType.ImageView, view.Handle),
                    generation);
                if (view.Handle == 0 ||
                    generation == 0 ||
                    !_retiredImageViewHandlesAll.Add(viewKey))
                {
                    continue;
                }

                _retiredImageViewHandles[frameSlot].Add(viewKey);
                filtered ??= new List<ImageView>(views.Length);
                filteredGenerationList ??= new List<ulong>(views.Length);
                filtered.Add(view);
                filteredGenerationList.Add(generation);
            }

            if (filtered is null)
            {
                filteredGenerations = [];
                return [];
            }

            filteredGenerations = [.. filteredGenerationList!];
            return [.. filtered];
        }

        internal void RetireSampler(Sampler sampler)
        {
            if (sampler.Handle == 0)
                return;

            RetireImageResources(new RetiredImageResources(
                default,
                default,
                default,
                [],
                sampler,
                0));
        }

        /// <summary>
        /// Destroys all image resources that were retired during the last use of
        /// the current frame slot.  Called immediately after <c>WaitForFences</c>.
        /// </summary>
        private void DrainRetiredImages(int maxItems = RetiredImageDrainLimitPerFrame)
        {
            DrainRetiredImages(currentFrame, maxItems);
        }

        private void DrainRetiredImages(int frameSlot, int maxItems)
        {
            RetiredImageResourceEntry[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                List<RetiredImageResourceEntry> list = _retiredImages[frameSlot];
                int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (capacity == 0)
                    return;

                List<RetiredImageResourceEntry> ready = new(capacity);
                for (int i = 0; i < list.Count && ready.Count < capacity;)
                {
                    RetiredImageResourceEntry candidate = list[i];
                    if (!IsVulkanRetirementReady(candidate.Ticket) ||
                        HasUndestroyedVulkanImageDependency(candidate.Resources))
                    {
                        i++;
                        continue;
                    }

                    ready.Add(candidate);
                    list.RemoveAt(i);
                }
                retired = [.. ready];
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("images", frameSlot, remaining);

            int destroyedImages = 0;
            int freedMemories = 0;
            int destroyedViews = 0;
            int destroyedSamplers = 0;
            long destroyedImageBytes = 0;
            foreach (RetiredImageResourceEntry entry in retired)
            {
                RetiredImageResources r = entry.Resources;
                bool canDestroyImage = r.Image.Handle != 0 &&
                    TryBeginDestroyVulkanResourceGeneration(
                        ObjectType.Image,
                        r.Image.Handle,
                        entry.ImageGeneration,
                        "DrainRetiredImages.Image");
                bool canDestroySampler = r.Sampler.Handle != 0 &&
                    TryBeginDestroyVulkanResourceGeneration(
                        ObjectType.Sampler,
                        r.Sampler.Handle,
                        entry.SamplerGeneration,
                        "DrainRetiredImages.Sampler");
                bool hasTrackedImageAllocation = false;
                VulkanMemoryAllocation trackedImageAllocation = default;
                if (canDestroyImage)
                {
                    hasTrackedImageAllocation = _imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation);
                    UntrackImageAllocation(r.Image);
                }

                if (canDestroySampler)
                {
                    Api!.DestroySampler(device, r.Sampler, null);
                    CompleteVulkanResourceDestruction(ObjectType.Sampler, r.Sampler.Handle);
                    UnregisterLiveSampler(r.Sampler);
                    destroyedSamplers++;
                }
                if (r.PrimaryView.Handle != 0)
                {
                    if (TryBeginDestroyImageViewGeneration(
                            r.PrimaryView,
                            entry.PrimaryViewGeneration,
                            "DrainRetiredImages.PrimaryView"))
                    {
                        Api!.DestroyImageView(device, r.PrimaryView, null);
                        CompleteVulkanResourceDestruction(ObjectType.ImageView, r.PrimaryView.Handle);
                        destroyedViews++;
                    }
                }
                if (r.AttachmentViews is not null)
                {
                    for (int i = 0; i < r.AttachmentViews.Length; i++)
                    {
                        ImageView v = r.AttachmentViews[i];
                        if (v.Handle != 0)
                        {
                            ulong viewGeneration = i < entry.AttachmentViewGenerations.Length
                                ? entry.AttachmentViewGenerations[i]
                                : 0;
                            if (TryBeginDestroyImageViewGeneration(
                                    v,
                                    viewGeneration,
                                    "DrainRetiredImages.AttachmentView"))
                            {
                                Api!.DestroyImageView(device, v, null);
                                CompleteVulkanResourceDestruction(ObjectType.ImageView, v.Handle);
                                destroyedViews++;
                            }
                        }
                    }
                }
                if (canDestroyImage)
                {
                    Api!.DestroyImage(device, r.Image, null);
                    CompleteVulkanResourceDestruction(ObjectType.Image, r.Image.Handle);
                    destroyedImages++;
                    if (r.AllocatedVRAMBytes > 0)
                        destroyedImageBytes += r.AllocatedVRAMBytes;
                }
                if (canDestroyImage)
                    _retiringImageHandles.TryRemove(r.Image.Handle, out _);

                // Image memory is allocator-owned. A missing allocation record means the
                // entry is stale or ownership was already transferred; never raw-free it.
                if (canDestroyImage && hasTrackedImageAllocation && trackedImageAllocation.Memory.Handle != 0)
                {
                    FreeMemoryAllocation(trackedImageAllocation);
                    freedMemories++;
                }
                else if (r.Memory.Handle != 0 && (!canDestroyImage || !hasTrackedImageAllocation))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Retirement.SkipUnownedImageMemory.{r.Memory.Handle}",
                        TimeSpan.FromSeconds(5),
                        "[Vulkan.ResourceLifetime] Skipping raw vkFreeMemory for unowned/stale image memory 0x{0:X}; image=0x{1:X} generation={2} trackedAllocation={3}.",
                        r.Memory.Handle,
                        r.Image.Handle,
                        entry.ImageGeneration,
                        hasTrackedImageAllocation);
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

        private void CompleteRetiredImageDeduplication(
            int frameSlot,
            in RetiredImageResourceEntry entry)
        {
            RetiredImageResources resources = entry.Resources;
            lock (_retiredResourceLock)
            {
                if (resources.Image.Handle != 0)
                {
                    _retiredImageHandles[frameSlot].Remove(resources.Image.Handle);
                    _retiredImageHandlesAll.Remove(resources.Image.Handle);
                }
                if (resources.Memory.Handle != 0)
                {
                    _retiredImageMemoryHandles[frameSlot].Remove(resources.Memory.Handle);
                    _retiredImageMemoryHandlesAll.Remove(resources.Memory.Handle);
                }
                if (resources.PrimaryView.Handle != 0)
                {
                    VulkanPinnedResourceGeneration primaryViewKey = new(
                        ResourceKey(ObjectType.ImageView, resources.PrimaryView.Handle),
                        entry.PrimaryViewGeneration);
                    _retiredImageViewHandles[frameSlot].Remove(primaryViewKey);
                    _retiredImageViewHandlesAll.Remove(primaryViewKey);
                }
                if (resources.AttachmentViews is not null)
                {
                    for (int i = 0; i < resources.AttachmentViews.Length; i++)
                    {
                        ulong handle = resources.AttachmentViews[i].Handle;
                        if (handle == 0)
                            continue;
                        ulong generation = i < entry.AttachmentViewGenerations.Length
                            ? entry.AttachmentViewGenerations[i]
                            : 0;
                        VulkanPinnedResourceGeneration attachmentViewKey = new(
                            ResourceKey(ObjectType.ImageView, handle),
                            generation);
                        _retiredImageViewHandles[frameSlot].Remove(attachmentViewKey);
                        _retiredImageViewHandlesAll.Remove(attachmentViewKey);
                    }
                }
                if (resources.Sampler.Handle != 0)
                {
                    _retiredSamplerHandles[frameSlot].Remove(resources.Sampler.Handle);
                    _retiredSamplerHandlesAll.Remove(resources.Sampler.Handle);
                }
            }
        }

        /// <summary>
        /// Blocks until all in-flight frame slots have completed their GPU work.
        /// Used before destroying resources that may be referenced by command
        /// buffers from other (non-current) frame slots.
        /// </summary>
        internal void WaitForAllInFlightWork()
        {
            if (_frameSlotTimelineValues is null || _graphicsTimelineSemaphore.Handle == 0)
                return;

            RuntimeRenderingHostServices.Current.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(GlobalInFlightWaits: 1));
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                ulong value = _frameSlotTimelineValues[i];
                if (value > 0)
                    WaitForTimelineValue(_graphicsTimelineSemaphore, value);
            }
        }

        /// <summary>
        /// Immediately destroys all retired resources across ALL frame slots.
        /// Call only after <c>DeviceWaitIdle</c> (e.g. during shutdown) to ensure
        /// no in-flight command buffers reference the resources.
        /// </summary>
        internal void ForceFlushAllRetiredResources()
        {
            RuntimeRenderingHostServices.Current.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(ForceFlushes: 1));
            bool forcedAfterDeviceLoss = IsDeviceLost;
            if (forcedAfterDeviceLoss)
            {
                Debug.VulkanWarning(
                    "[Vulkan.ResourceLifetime] Force-destroying retired resources after device loss without waiting for timelines or fences.");
            }

            // Every caller has either waited for the device to become idle or has
            // explicitly waited for all in-flight frame work. Retirement pins are
            // frame-safety guards, so retaining them past that boundary leaks the
            // descriptor pools (and their sets) into vkDestroyDevice.
            BeginForcedVulkanRetirementDrain();
            try
            {
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredCommandBuffers(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredDescriptorSets(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredDescriptorPools(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredPipelines(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredPipelineLayouts(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredQueryPools(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredBufferViews(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredFramebuffers(i, int.MaxValue);
                for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                    DrainRetiredBuffers(i, int.MaxValue);
                for (int pass = 0; pass < MAX_FRAMES_IN_FLIGHT; pass++)
                    for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                        DrainRetiredImages(i, int.MaxValue);
            }
            finally
            {
                EndForcedVulkanRetirementDrain();
            }

            LogVulkanResourceLifetimeDiagnostics(
                forcedAfterDeviceLoss ? "device-loss-force-destroy" : "force-flush-completed");
        }

        internal void ForceFlushAllRetiredResourcesAfterWaiting(string reason)
        {
            if (IsDeviceLost)
                return;

            Debug.VulkanEvery(
                $"Vulkan.RetiredResources.ForceFlushAfterWait.{GetHashCode()}.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Waiting for in-flight work before force-flushing retired resources: reason={0}.",
                reason);

            WaitForAllInFlightWork();
            ForceFlushAllRetiredResources();
        }

        /// <summary>
        /// Immediately destroys completed non-image resources across all frame slots.
        /// Images stay on the normal frame-slot drain path because descriptors can
        /// still carry old image handles until the next material/FBO refresh.
        /// </summary>
        internal void ForceFlushCompletedNonImageRetiredResources()
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredCommandBuffers(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredDescriptorSets(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredDescriptorPools(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredPipelines(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredPipelineLayouts(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredQueryPools(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredBufferViews(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredFramebuffers(i, int.MaxValue);
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
                DrainRetiredBuffers(i, int.MaxValue);
        }
    }
}
