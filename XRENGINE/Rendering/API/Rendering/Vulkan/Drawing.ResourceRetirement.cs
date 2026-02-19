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
                if (buf.Handle != 0 && destroyedBuffers.Add(buf.Handle))
                    Api!.DestroyBuffer(device, buf, null);

                if (mem.Handle != 0 && freedMemories.Add(mem.Handle))
                    Api!.FreeMemory(device, mem, null);
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

        /// <summary>
        /// Queues image resources for deferred destruction.  The resources will be
        /// destroyed the next time this frame slot is reused (after the timeline
        /// wait guarantees the GPU is done with them).
        /// </summary>
        internal void RetireImageResources(in RetiredImageResources resources)
            => _retiredImages[currentFrame].Add(resources);

        /// <summary>
        /// Destroys all image resources that were retired during the last use of
        /// the current frame slot.  Called immediately after <c>WaitForFences</c>.
        /// </summary>
        private void DrainRetiredImages()
        {
            var list = _retiredImages[currentFrame];
            if (list.Count == 0)
                return;

            foreach (var r in list)
            {
                if (r.Sampler.Handle != 0)
                    Api!.DestroySampler(device, r.Sampler, null);
                if (r.PrimaryView.Handle != 0)
                    Api!.DestroyImageView(device, r.PrimaryView, null);
                if (r.AttachmentViews is not null)
                {
                    foreach (ImageView v in r.AttachmentViews)
                        if (v.Handle != 0)
                            Api!.DestroyImageView(device, v, null);
                }
                if (r.Image.Handle != 0)
                    Api!.DestroyImage(device, r.Image, null);
                if (r.Memory.Handle != 0)
                    Api!.FreeMemory(device, r.Memory, null);
            }
            list.Clear();
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
                DrainRetiredBuffers();
                DrainRetiredImages();
                DrainRetiredFramebuffers();
            }
            currentFrame = saved;
        }
    }
}
