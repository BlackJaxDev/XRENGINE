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

        private readonly HashSet<ulong>[] _retiredMemoryHandles =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        private readonly object _retiredResourceLock = new();

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

        private void DrainRetiredPipelines()
        {
            int frameSlot = currentFrame;
            Pipeline[] retired;

            lock (_retiredResourceLock)
            {
                var list = _retiredPipelines[frameSlot];
                if (list.Count == 0)
                    return;

                retired = [.. list];
                list.Clear();
                _retiredPipelineHandles[frameSlot].Clear();
            }

            if (Api is null || device.Handle == 0)
                return;

            foreach (Pipeline pipeline in retired)
            {
                if (pipeline.Handle != 0)
                    Api!.DestroyPipeline(device, pipeline, null);
            }
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
            => DrainRetiredDescriptorPools(currentFrame);

        private void DrainRetiredDescriptorPools(int frameSlot)
        {
            DescriptorPool[] retired;

            lock (_retiredResourceLock)
            {
                var list = _retiredDescriptorPools[frameSlot];
                if (list.Count == 0)
                    return;

                retired = [.. list];
                list.Clear();
                _retiredDescriptorPoolHandles[frameSlot].Clear();
            }

            if (Api is null || device.Handle == 0)
                return;

            foreach (DescriptorPool pool in retired)
            {
                if (pool.Handle == 0)
                    continue;

                Api!.DestroyDescriptorPool(device, pool, null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
            }
        }

        internal void DrainAllRetiredDescriptorPools()
        {
            for (int frameSlot = 0; frameSlot < _retiredDescriptorPools.Length; frameSlot++)
                DrainRetiredDescriptorPools(frameSlot);
        }

        internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction(string reason)
        {
            int meshRendererCount = 0;
            int materialCount = 0;
            int computeCachedPoolCount = ReleaseComputeDescriptorReferencesForPhysicalResourceDestruction();
            int computeTransientPoolCount = ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction();

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
                }
            }

            DrainAllRetiredDescriptorPools();
            MarkCommandBuffersDirty();

            Debug.VulkanEvery(
                $"Vulkan.ResourceDestroy.ReleaseDescriptorReferences.{reason}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Released descriptor references before physical resource destruction: reason={0} meshRenderers={1} materials={2} computeCachedPools={3} computeTransientPools={4}.",
                reason,
                meshRendererCount,
                materialCount,
                computeCachedPoolCount,
                computeTransientPoolCount);
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
        private void DrainRetiredFramebuffers()
        {
            List<Framebuffer> list;
            lock (_retiredResourceLock)
            {
                list = _retiredFramebuffers[currentFrame];
                if (list.Count == 0)
                    return;
            }

            // Copy under lock, then destroy outside.
            Framebuffer[] retired;
            lock (_retiredResourceLock)
            {
                retired = [.. list];
                list.Clear();
                _retiredFramebufferHandles[currentFrame].Clear();
            }

            if (Api is null || device.Handle == 0)
                return;

            foreach (Framebuffer fb in retired)
            {
                if (fb.Handle != 0)
                    Api!.DestroyFramebuffer(device, fb, null);
            }
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
        private void DrainRetiredBuffers()
        {
            int frameSlot = currentFrame;
            (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)[] retired;

            lock (_retiredResourceLock)
            {
                var list = _retiredBuffers[frameSlot];
                if (list.Count == 0)
                    return;

                retired = [.. list];
                list.Clear();
                _retiredBufferHandles[frameSlot].Clear();
                _retiredMemoryHandles[frameSlot].Clear();
            }

            if (Api is null || device.Handle == 0)
                return;

            var destroyedBuffers = new HashSet<ulong>();
            var freedMemories = new HashSet<ulong>();

            foreach (var (buf, mem) in retired)
            {
                DeviceMemory memory = mem;

                // Free through allocator if tracked, otherwise direct FreeMemory.
                if (buf.Handle != 0 && destroyedBuffers.Add(buf.Handle))
                {
                    if (_bufferAllocations.TryRemove(buf.Handle, out VulkanMemoryAllocation allocation))
                    {
                        Api!.DestroyBuffer(device, buf, null);
                        FreeMemoryAllocation(allocation);
                        continue;
                    }

                    if (_legacyBufferAllocations.TryRemove(buf.Handle, out VulkanMemoryAllocation legacyAllocation) && memory.Handle == 0)
                        memory = legacyAllocation.Memory;

                    Api!.DestroyBuffer(device, buf, null);
                }

                if (memory.Handle != 0 && freedMemories.Add(memory.Handle))
                    Api!.FreeMemory(device, memory, null);
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

                if (image.Handle != 0 && !_retiredImageHandles[frameSlot].Add(image.Handle))
                    image = default;

                if (memory.Handle != 0 && !_retiredImageMemoryHandles[frameSlot].Add(memory.Handle))
                    memory = default;

                if (primaryView.Handle != 0 && !_retiredImageViewHandles[frameSlot].Add(primaryView.Handle))
                    primaryView = default;

                ImageView[] attachmentViews = FilterRetiredAttachmentViews(resources.AttachmentViews, frameSlot);

                if (sampler.Handle != 0 && !_retiredSamplerHandles[frameSlot].Add(sampler.Handle))
                    sampler = default;

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
        private void DrainRetiredImages()
        {
            int frameSlot = currentFrame;
            RetiredImageResources[] retired;

            lock (_retiredResourceLock)
            {
                var list = _retiredImages[frameSlot];
                if (list.Count == 0)
                    return;

                retired = [.. list];
                list.Clear();
                _retiredImageHandles[frameSlot].Clear();
                _retiredImageMemoryHandles[frameSlot].Clear();
                _retiredImageViewHandles[frameSlot].Clear();
                _retiredSamplerHandles[frameSlot].Clear();
            }

            HashSet<ulong> destroyedImages = new();
            HashSet<ulong> freedMemories = new();
            HashSet<ulong> destroyedViews = new();
            HashSet<ulong> destroyedSamplers = new();

            foreach (var r in retired)
            {
                bool hasTrackedImageAllocation = false;
                VulkanMemoryAllocation trackedImageAllocation = default;
                if (r.Image.Handle != 0)
                    hasTrackedImageAllocation = _imageAllocations.TryRemove(r.Image.Handle, out trackedImageAllocation);

                if (r.Sampler.Handle != 0 && destroyedSamplers.Add(r.Sampler.Handle))
                    Api!.DestroySampler(device, r.Sampler, null);
                if (r.PrimaryView.Handle != 0 && destroyedViews.Add(r.PrimaryView.Handle))
                    Api!.DestroyImageView(device, r.PrimaryView, null);
                if (r.AttachmentViews is not null)
                {
                    foreach (ImageView v in r.AttachmentViews)
                        if (v.Handle != 0 && destroyedViews.Add(v.Handle))
                            Api!.DestroyImageView(device, v, null);
                }
                if (r.Image.Handle != 0 && destroyedImages.Add(r.Image.Handle))
                    Api!.DestroyImage(device, r.Image, null);

                // Free through allocator if tracked, otherwise direct FreeMemory.
                DeviceMemory memory = hasTrackedImageAllocation ? trackedImageAllocation.Memory : r.Memory;
                if (memory.Handle != 0 && freedMemories.Add(memory.Handle))
                {
                    if (hasTrackedImageAllocation)
                        FreeMemoryAllocation(trackedImageAllocation);
                    else
                        Api!.FreeMemory(device, memory, null);
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
            int saved = currentFrame;
            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                currentFrame = i;
                DrainRetiredDescriptorPools();
                DrainRetiredPipelines();
                DrainRetiredBuffers();
                DrainRetiredFramebuffers();
                DrainRetiredImages();
            }
            currentFrame = saved;
        }
    }
}
