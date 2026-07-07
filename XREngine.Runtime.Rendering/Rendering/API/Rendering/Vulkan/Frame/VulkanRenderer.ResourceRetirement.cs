using Silk.NET.Vulkan;

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
        private readonly List<(Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)>[] _retiredBuffers =
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

        private readonly object _retiredResourceLock = new();
        private const int RetiredDescriptorPoolDrainLimitPerFrame = 8;
        private const int RetiredPipelineDrainLimitPerFrame = 8;
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
        private readonly List<Framebuffer>[] _retiredFramebuffers =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredFramebufferHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        // =========== Descriptor Pool Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for descriptor pools whose descriptor
        /// sets may still be referenced by previously recorded command buffers.
        /// </summary>
        private readonly List<DescriptorPool>[] _retiredDescriptorPools =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredDescriptorPoolHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        // =========== Pipeline Retirement ===========

        /// <summary>
        /// Per-frame-slot retirement queue for pipelines whose handles may still
        /// be referenced by command buffers recorded earlier in the same frame or
        /// by previously submitted frame slots.
        /// </summary>
        private readonly List<Pipeline>[] _retiredPipelines =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredPipelineHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        internal void RetirePipeline(Pipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (!_retiredPipelineHandles[frameSlot].Add(pipeline.Handle))
                    return;

                _retiredPipelines[frameSlot].Add(pipeline);
            }
        }

        private void DrainRetiredPipelines(int maxItems = RetiredPipelineDrainLimitPerFrame)
        {
            DrainRetiredPipelines(currentFrame, maxItems);
        }

        private void DrainRetiredPipelines(int frameSlot, int maxItems)
        {
            Pipeline[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                var list = _retiredPipelines[frameSlot];
                int drainCount = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (drainCount == 0)
                    return;

                retired = new Pipeline[drainCount];
                list.CopyTo(0, retired, 0, drainCount);
                list.RemoveRange(0, drainCount);
                foreach (Pipeline pipeline in retired)
                {
                    if (pipeline.Handle != 0)
                        _retiredPipelineHandles[frameSlot].Remove(pipeline.Handle);
                }
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("pipelines", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedPipelines = 0;
            foreach (Pipeline pipeline in retired)
            {
                if (pipeline.Handle != 0)
                {
                    Api!.DestroyPipeline(device, pipeline, null);
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
                if (!_retiredDescriptorPoolHandles[frameSlot].Add(descriptorPool.Handle))
                    return;

                _retiredDescriptorPools[frameSlot].Add(descriptorPool);
            }
        }

        private void DrainRetiredDescriptorPools()
            => DrainRetiredDescriptorPools(currentFrame, RetiredDescriptorPoolDrainLimitPerFrame);

        private void DrainRetiredDescriptorPools(int frameSlot, int maxItems = int.MaxValue)
        {
            DescriptorPool[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                var list = _retiredDescriptorPools[frameSlot];
                int drainCount = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (drainCount == 0)
                    return;

                retired = new DescriptorPool[drainCount];
                list.CopyTo(0, retired, 0, drainCount);
                list.RemoveRange(0, drainCount);
                foreach (DescriptorPool pool in retired)
                {
                    if (pool.Handle != 0)
                        _retiredDescriptorPoolHandles[frameSlot].Remove(pool.Handle);
                }
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("descriptor pools", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            // Descriptor sets allocated from these pools can be referenced by cached
            // primary variants for any frame slot.  A current-slot wait is not enough
            // when resource-plan replacement invalidates descriptor state globally.
            WaitForAllInFlightWork();

            int destroyedPools = 0;
            foreach (DescriptorPool pool in retired)
            {
                if (pool.Handle == 0)
                    continue;

                Api!.DestroyDescriptorPool(device, pool, null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
                destroyedPools++;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(descriptorPools: destroyedPools);
        }

        internal void DrainAllRetiredDescriptorPools()
        {
            for (int frameSlot = 0; frameSlot < _retiredDescriptorPools.Length; frameSlot++)
                DrainRetiredDescriptorPools(frameSlot, int.MaxValue);
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

            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (!_retiredFramebufferHandles[frameSlot].Add(framebuffer.Handle))
                    return;

                _retiredFramebuffers[frameSlot].Add(framebuffer);
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
            Framebuffer[] retired;
            int remaining;
            lock (_retiredResourceLock)
            {
                List<Framebuffer> list = _retiredFramebuffers[frameSlot];
                int drainCount = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (drainCount == 0)
                    return;

                retired = new Framebuffer[drainCount];
                list.CopyTo(0, retired, 0, drainCount);
                list.RemoveRange(0, drainCount);
                foreach (Framebuffer framebuffer in retired)
                {
                    if (framebuffer.Handle != 0)
                        _retiredFramebufferHandles[frameSlot].Remove(framebuffer.Handle);
                }
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("framebuffers", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedFramebuffers = 0;
            foreach (Framebuffer fb in retired)
            {
                if (fb.Handle != 0)
                {
                    Api!.DestroyFramebuffer(device, fb, null);
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

            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                if (buffer.Handle != 0 && !_retiredBufferHandlesAll.Add(buffer.Handle))
                    return;

                if (buffer.Handle != 0 && !_retiredBufferHandles[frameSlot].Add(buffer.Handle))
                    buffer = default;

                if (memory.Handle != 0 && !_retiredMemoryHandles[frameSlot].Add(memory.Handle))
                    memory = default;

                if (buffer.Handle == 0 && memory.Handle == 0)
                    return;

                _retiredBuffers[frameSlot].Add((buffer, memory));
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
            (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                var list = _retiredBuffers[frameSlot];
                int drainCount = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (drainCount == 0)
                    return;

                retired = new (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)[drainCount];
                list.CopyTo(0, retired, 0, drainCount);
                list.RemoveRange(0, drainCount);
                foreach (var (buffer, memory) in retired)
                {
                    if (buffer.Handle != 0)
                    {
                        _retiredBufferHandles[frameSlot].Remove(buffer.Handle);
                        _retiredBufferHandlesAll.Remove(buffer.Handle);
                    }
                    if (memory.Handle != 0)
                        _retiredMemoryHandles[frameSlot].Remove(memory.Handle);
                }
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("buffers", frameSlot, remaining);

            if (Api is null || device.Handle == 0)
                return;

            int destroyedBuffers = 0;
            int freedMemories = 0;
            int pooledBuffers = 0;
            foreach (var (buf, mem) in retired)
            {
                DeviceMemory memory = mem;

                // Prefer tracked ownership so allocator-backed memory is never raw-freed.
                if (buf.Handle != 0)
                {
                    if (memory.Handle != 0 && _stagingManager.TryRelease(buf, memory))
                    {
                        pooledBuffers++;
                        continue;
                    }

                    if (TryDestroyKnownBufferAllocation(buf, out bool destroyedTrackedBuffer, out bool freedTrackedMemory))
                    {
                        if (destroyedTrackedBuffer)
                            destroyedBuffers++;
                        if (freedTrackedMemory)
                            freedMemories++;
                        continue;
                    }

                    if (TryBeginDestroyBuffer(buf, "DrainRetiredBuffers.Untracked"))
                    {
                        Api!.DestroyBuffer(device, buf, null);
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
                Debug.VulkanEvery(
                    $"Vulkan.TextureUpload.StagingReturnedToPool.{GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Returned {0} retired staging buffer(s) to the Vulkan staging pool for reuse.",
                    pooledBuffers);
            }
        }

        /// <summary>
        /// Holds a complete set of Vulkan handles that were owned by a
        /// <see cref="VkImageBackedTexture{T}"/> or <see cref="VulkanPhysicalImageGroup"/>
        /// and need deferred destruction.  Kept alive until the frame slot's
        /// timeline fence signals that no in-flight command buffer references them.
        /// </summary>
        internal readonly record struct RetiredImageResources(
            Image Image,
            DeviceMemory Memory,
            ImageView PrimaryView,
            ImageView[] AttachmentViews,
            Sampler Sampler,
            long AllocatedVRAMBytes);

        /// <summary>
        /// Per-frame-slot retirement queue for image resources that cannot be
        /// destroyed immediately because an in-flight command buffer may still
        /// reference them.  Drained alongside <see cref="_retiredBuffers"/>.
        /// </summary>
        private readonly List<RetiredImageResources>[] _retiredImages =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredImageHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredImageMemoryHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredImageViewHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly HashSet<ulong>[] _retiredSamplerHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        /// <summary>
        /// Queues image resources for deferred destruction.  The resources will be
        /// destroyed the next time this frame slot is reused (after the timeline
        /// wait guarantees the GPU is done with them).
        /// </summary>
        internal void RetireImageResources(in RetiredImageResources resources)
        {
            int frameSlot = currentFrame;

            lock (_retiredResourceLock)
            {
                Image image = resources.Image;
                DeviceMemory memory = resources.Memory;
                ImageView primaryView = resources.PrimaryView;
                Sampler sampler = resources.Sampler;

                if (image.Handle != 0)
                {
                    ClearTrackedImageLayouts(image);
                    _retiringImageHandles[image.Handle] = 0;
                }

                if (image.Handle != 0 && !_retiredImageHandles[frameSlot].Add(image.Handle))
                    image = default;

                if (memory.Handle != 0 && !_retiredImageMemoryHandles[frameSlot].Add(memory.Handle))
                    memory = default;

                if (primaryView.Handle != 0 && !_retiredImageViewHandles[frameSlot].Add(primaryView.Handle))
                    primaryView = default;

                ImageView[] attachmentViews = FilterRetiredAttachmentViews(resources.AttachmentViews, frameSlot);

                if (sampler.Handle != 0 && !_retiredSamplerHandles[frameSlot].Add(sampler.Handle))
                    sampler = default;
                else if (sampler.Handle != 0)
                    UnregisterLiveSampler(sampler);

                if (image.Handle == 0 &&
                    memory.Handle == 0 &&
                    primaryView.Handle == 0 &&
                    attachmentViews.Length == 0 &&
                    sampler.Handle == 0)
                {
                    return;
                }

                _retiredImages[frameSlot].Add(new RetiredImageResources(
                    image,
                    memory,
                    primaryView,
                    attachmentViews,
                    sampler,
                    resources.AllocatedVRAMBytes));
            }
        }

        private ImageView[] FilterRetiredAttachmentViews(ImageView[]? views, int frameSlot)
        {
            if (views is null || views.Length == 0)
                return [];

            List<ImageView>? filtered = null;
            foreach (ImageView view in views)
            {
                if (view.Handle == 0 || !_retiredImageViewHandles[frameSlot].Add(view.Handle))
                    continue;

                filtered ??= new List<ImageView>(views.Length);
                filtered.Add(view);
            }

            return filtered is null ? [] : [.. filtered];
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
            RetiredImageResources[] retired;
            int remaining;

            lock (_retiredResourceLock)
            {
                var list = _retiredImages[frameSlot];
                int drainCount = GetRetiredResourceDrainCount(list.Count, maxItems);
                if (drainCount == 0)
                    return;

                retired = new RetiredImageResources[drainCount];
                list.CopyTo(0, retired, 0, drainCount);
                list.RemoveRange(0, drainCount);
                foreach (RetiredImageResources resources in retired)
                {
                    if (resources.Image.Handle != 0)
                        _retiredImageHandles[frameSlot].Remove(resources.Image.Handle);
                    if (resources.Memory.Handle != 0)
                        _retiredImageMemoryHandles[frameSlot].Remove(resources.Memory.Handle);
                    if (resources.PrimaryView.Handle != 0)
                        _retiredImageViewHandles[frameSlot].Remove(resources.PrimaryView.Handle);
                    if (resources.AttachmentViews is not null)
                    {
                        foreach (ImageView view in resources.AttachmentViews)
                        {
                            if (view.Handle != 0)
                                _retiredImageViewHandles[frameSlot].Remove(view.Handle);
                        }
                    }
                    if (resources.Sampler.Handle != 0)
                        _retiredSamplerHandles[frameSlot].Remove(resources.Sampler.Handle);
                }
                remaining = list.Count;
            }

            ReportRetiredResourceBacklog("images", frameSlot, remaining);

            int destroyedImages = 0;
            int freedMemories = 0;
            int destroyedViews = 0;
            int destroyedSamplers = 0;
            long destroyedImageBytes = 0;
            foreach (var r in retired)
            {
                bool hasTrackedImageAllocation = false;
                VulkanMemoryAllocation trackedImageAllocation = default;
                if (r.Image.Handle != 0)
                    hasTrackedImageAllocation = _imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation);

                if (r.Sampler.Handle != 0)
                {
                    Api!.DestroySampler(device, r.Sampler, null);
                    destroyedSamplers++;
                }
                if (r.PrimaryView.Handle != 0)
                {
                    if (TryBeginDestroyImageView(r.PrimaryView, "DrainRetiredImages.PrimaryView"))
                    {
                        Api!.DestroyImageView(device, r.PrimaryView, null);
                        destroyedViews++;
                    }
                }
                if (r.AttachmentViews is not null)
                {
                    foreach (ImageView v in r.AttachmentViews)
                    {
                        if (v.Handle != 0)
                        {
                            if (TryBeginDestroyImageView(v, "DrainRetiredImages.AttachmentView"))
                            {
                                Api!.DestroyImageView(device, v, null);
                                destroyedViews++;
                            }
                        }
                    }
                }
                if (r.Image.Handle != 0)
                {
                    Api!.DestroyImage(device, r.Image, null);
                    destroyedImages++;
                    if (r.AllocatedVRAMBytes > 0)
                        destroyedImageBytes += r.AllocatedVRAMBytes;
                }
                if (r.Image.Handle != 0)
                    _retiringImageHandles.TryRemove(r.Image.Handle, out _);

                // Free through allocator if tracked, otherwise direct FreeMemory.
                DeviceMemory memory = hasTrackedImageAllocation ? trackedImageAllocation.Memory : r.Memory;
                if (memory.Handle != 0)
                {
                    if (hasTrackedImageAllocation)
                        FreeMemoryAllocation(trackedImageAllocation);
                    else
                        Api!.FreeMemory(device, memory, null);
                    freedMemories++;
                }
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
                images: destroyedImages,
                imageViews: destroyedViews,
                samplers: destroyedSamplers,
                imageMemories: freedMemories,
                imageBytes: destroyedImageBytes);
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
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                DrainRetiredDescriptorPools(i, int.MaxValue);
                DrainRetiredPipelines(i, int.MaxValue);
                DrainRetiredBuffers(i, int.MaxValue);
                DrainRetiredFramebuffers(i, int.MaxValue);
                DrainRetiredImages(i, int.MaxValue);
            }
        }

        /// <summary>
        /// Immediately destroys completed non-image resources across all frame slots.
        /// Images stay on the normal frame-slot drain path because descriptors can
        /// still carry old image handles until the next material/FBO refresh.
        /// </summary>
        internal void ForceFlushCompletedNonImageRetiredResources()
        {
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                DrainRetiredDescriptorPools(i, int.MaxValue);
                DrainRetiredPipelines(i, int.MaxValue);
                DrainRetiredBuffers(i, int.MaxValue);
                DrainRetiredFramebuffers(i, int.MaxValue);
            }
        }
    }
}
