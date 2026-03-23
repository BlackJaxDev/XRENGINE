using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using System;
using System.Linq;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer(XRWindow window, bool shouldLinkWindow = true) : AbstractRenderer<Vk>(window, shouldLinkWindow)
    {
        protected override Vk GetAPI()
            => Vk.GetApi();

        public override void Initialize()
        {
            if (Window?.VkSurface is null)
                throw new Exception("Windowing platform doesn't support Vulkan.");

            CreateInstance();
            SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateCommandPool();

            CreateDescriptorSetLayout();
            CreateAllSwapChainObjects();

            //CreateTestModel();
            //CreateUniformBuffers();

            CreateSyncObjects();
            CreateFrameTimingResources();
            InitializeMemoryAllocator();
            InitializeSynchronizationBackend();
            InitializeDynamicUniformRingBuffers();
        }

        /// <summary>
        /// Whether any device memory type supports <see cref="MemoryPropertyFlags.LazilyAllocatedBit"/>.
        /// True on most mobile/tiler GPUs; false on typical discrete desktop GPUs.
        /// </summary>
        internal bool SupportsLazyAllocation { get; private set; }

        private void InitializeMemoryAllocator()
        {
            // Probe for lazy allocation support (TransientAttachment optimization).
            Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProps);
            for (int i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if (memProps.MemoryTypes[i].PropertyFlags.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
                {
                    SupportsLazyAllocation = true;
                    break;
                }
            }

            EVulkanAllocatorBackend backend = Engine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend;
            _memoryAllocator = backend switch
            {
                EVulkanAllocatorBackend.Suballocator => new VulkanBlockAllocator(this),
                _ => new VulkanLegacyAllocator(this),
            };
            Debug.Vulkan($"[Vulkan] Memory allocator initialized: {backend} (lazyAlloc={SupportsLazyAllocation})");
        }

        public override void CleanUp()
        {
            if (device.Handle != 0)
                DeviceWaitIdle();

            WaitForPendingReadbackTasks(TimeSpan.FromSeconds(6));
            DestroyComputeTransientResources();
            DestroyDanglingMaterialWrappers();
            DestroyDanglingMeshRendererWrappers();
            DestroyDanglingDataBufferWrappers();
            DestroyRemainingTrackedMeshUniformBuffers();

            // Drain all deferred-deletion queues now that the GPU is idle.
            ForceFlushAllRetiredResources();

            DestroyAutoExposureComputeResources();
            DestroyPlaceholderTexture();
            DisposeImGuiResources();
            DestroyAllSwapChainObjects();
            // FBO render passes are NOT destroyed during swapchain recreation
            // (they are swapchain-independent). Clean them up here at full shutdown.
            DestroyFrameBufferRenderPasses();
            DestroyDescriptorSetLayout();
            _resourceAllocator.DestroyPhysicalImages(this);
            _resourceAllocator.DestroyPhysicalBuffers(this);
            _stagingManager.Destroy(this);
            if (_memoryAllocator is VulkanBlockAllocator blockAllocator)
                blockAllocator.DestroyAllBlocks(Api!, device);
            _memoryAllocator?.Dispose();
            _memoryAllocator = null;
            _activeSynchronizationBackend = EVulkanSynchronizationBackend.Legacy;
            DestroyDynamicUniformRingBuffers();
            DestroyFrameTimingResources();

            DestroySyncObjects();
            DestroyCommandPool();

            // Run a final wrapper sweep after teardown paths that may have created or
            // retained late-bound GPU buffers during destruction.
            DestroyDanglingMaterialWrappers();
            DestroyDanglingMeshRendererWrappers();
            DestroyDanglingDataBufferWrappers();
            DestroyRemainingTrackedMeshUniformBuffers();

            // Teardown paths above may retire additional resources. Flush once more
            // before destroying the logical device to avoid vkDestroyDevice child-object errors.
            ForceFlushAllRetiredResources();

            DestroyLogicalDevice();
            DestroyValidationLayers();
            DestroySurface();
            DestroyInstance();
        }

        private void DestroyDanglingMaterialWrappers()
        {
            var wrappers = VkObject<XRMaterial>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingMeshRendererWrappers()
        {
            var wrappers = VkObject<XRMeshRenderer.BaseVersion>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        private void DestroyDanglingDataBufferWrappers()
        {
            var wrappers = VkObject<XRDataBuffer>.Cache.Values.ToArray();
            foreach (var wrapper in wrappers)
            {
                try
                {
                    wrapper?.Destroy();
                }
                catch
                {
                }
            }
        }

        // It should be noted that in a real world application, you're not supposed to actually call vkAllocateMemory for every individual buffer.
        // The maximum number of simultaneous memory allocations is limited by the maxMemoryAllocationCount physical device limit, which may be as low as 4096 even on high end hardware like an NVIDIA GTX 1080.
        // The right way to allocate memory for a large number of objects at the same time is to create a custom allocator that splits up a single allocation among many different objects by using the offset parameters that we've seen in many functions.

        private void AllocateMemory(MemoryAllocateInfo allocInfo, DeviceMemory* memPtr)
        {
            Result result = Api!.AllocateMemory(device, ref allocInfo, null, memPtr);
            if (result == Result.ErrorOutOfDeviceMemory || result == Result.ErrorOutOfHostMemory)
            {
                Debug.VulkanWarning(
                    $"[Vulkan] OOM during AllocateMemory (size={allocInfo.AllocationSize}, memType={allocInfo.MemoryTypeIndex}). Result={result}");
                throw new VulkanOutOfMemoryException(
                    $"Vulkan memory allocation failed ({result}). Size={allocInfo.AllocationSize}",
                    MemoryPropertyFlags.None);
            }
            if (result != Result.Success)
                throw new Exception($"Failed to allocate memory. Result={result}");
        }

        /// <summary>
        /// Attempts to allocate memory for a buffer through the active allocator,
        /// with automatic fallback to host-visible memory on OOM.
        /// </summary>
        internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(
            Buffer buffer, MemoryPropertyFlags requiredProperties)
        {
            IVulkanMemoryAllocator alloc = MemoryAllocator;
            if (alloc.TryAllocateForBuffer(Api!, device, buffer, requiredProperties, out VulkanMemoryAllocation allocation))
                return allocation;

            // OOM — attempt fallback to host-visible if the original was device-local.
            if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                Debug.VulkanWarning(
                    $"[Vulkan] OOM for buffer (requested {requiredProperties}). Falling back to {fallback}.");
                if (alloc.TryAllocateForBuffer(Api!, device, buffer, fallback, out allocation))
                {
                    Engine.Rendering.Stats.RecordVulkanOomFallback();
                    return allocation;
                }
            }

            throw new VulkanOutOfMemoryException(
                $"Vulkan buffer allocation failed with no viable fallback. Requested={requiredProperties}",
                requiredProperties);
        }

        /// <summary>
        /// Attempts to allocate memory for an image through the active allocator,
        /// with automatic fallback chain: requested → DeviceLocal (if lazy was requested) → HostVisible on OOM.
        /// Callers may include <see cref="MemoryPropertyFlags.LazilyAllocatedBit"/> for transient attachments;
        /// the allocator will strip it if the device doesn't support lazy allocation.
        /// </summary>
        internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(
            Image image, MemoryPropertyFlags requiredProperties)
        {
            IVulkanMemoryAllocator alloc = MemoryAllocator;

            // Strip lazy if device doesn't support it, to avoid guaranteed first-try failure.
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit) && !SupportsLazyAllocation)
                requiredProperties &= ~MemoryPropertyFlags.LazilyAllocatedBit;

            if (alloc.TryAllocateForImage(Api!, device, image, requiredProperties, out VulkanMemoryAllocation allocation))
                return allocation;

            // If lazy was requested, retry without it (device-local only).
            if (requiredProperties.HasFlag(MemoryPropertyFlags.LazilyAllocatedBit))
            {
                MemoryPropertyFlags withoutLazy = requiredProperties & ~MemoryPropertyFlags.LazilyAllocatedBit;
                if (alloc.TryAllocateForImage(Api!, device, image, withoutLazy, out allocation))
                    return allocation;
            }

            if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                Debug.VulkanWarning(
                    $"[Vulkan] OOM for image (requested {requiredProperties}). Falling back to {fallback}.");
                if (alloc.TryAllocateForImage(Api!, device, image, fallback, out allocation))
                {
                    Engine.Rendering.Stats.RecordVulkanOomFallback();
                    return allocation;
                }
            }

            throw new VulkanOutOfMemoryException(
                $"Vulkan image allocation failed with no viable fallback. Requested={requiredProperties}",
                requiredProperties);
        }

        /// <summary>Frees a memory allocation through the active allocator.</summary>
        internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
        {
            if (allocation.IsNull)
                return;
            MemoryAllocator.Free(Api!, device, allocation);
        }

        public static unsafe void* Allocated(void* pUserData, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        {
            //Output.Log();
            return null;
        }

        private void* Reallocated(void* pUserData, void* pOriginal, nuint size, nuint alignment, SystemAllocationScope allocationScope)
        {
            return null;
        }

        private void Freed(void* pUserData, void* pMemory)
        {

        }
        private void InternalAllocated(void* pUserData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope)
        {

        }

        private void InternalFreed(void* pUserData, nuint size, InternalAllocationType allocationType, SystemAllocationScope allocationScope)
        {

        }

        public override void StencilMask(uint mask)
        {
            _state.SetStencilWriteMask(mask);
            MarkCommandBuffersDirty();
        }

        public override void EnableStencilTest(bool enable)
        {
            // Vulkan: stencil test is configured per-pipeline; tracked in dynamic state for future use.
        }

        public override void StencilFunc(EComparison function, int reference, uint mask)
        {
            // Vulkan: stencil compare is per-pipeline state; no global toggle.
        }

        public override void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass)
        {
            // Vulkan: stencil ops are per-pipeline state; no global toggle.
        }

        public override void EnableBlend(bool enable)
        {
            // Vulkan: blend enable is per-pipeline state; no global toggle.
        }

        public override void BlendFunc(EBlendingFactor src, EBlendingFactor dst)
        {
            // Vulkan: blend factors are per-pipeline state; no global toggle.
        }

        public override void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha)
        {
            // Vulkan: blend factors are per-pipeline state; no global toggle.
        }

        public override void BlendEquation(EBlendEquationMode mode)
        {
            // Vulkan: blend equation is per-pipeline state; no global toggle.
        }

        public override void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha)
        {
            // Vulkan: blend equation is per-pipeline state; no global toggle.
        }

        public override void EnableSampleShading(float minValue)
        {
            // Vulkan: sample shading is configured per-pipeline, not a global state toggle.
            // Per-pipeline configuration would happen in VkMeshRenderer.Pipeline.cs.
        }
        public override void DisableSampleShading()
        {
            // Vulkan: sample shading is configured per-pipeline, not a global state toggle.
        }
        public override void AllowDepthWrite(bool v)
        {
            _state.SetDepthWriteEnabled(v);
            MarkCommandBuffersDirty();
        }
        public override void ClearDepth(float v)
        {
            _state.SetClearDepth(v);
            MarkCommandBuffersDirty();
        }
        public override void ClearStencil(int v)
        {
            _state.SetClearStencil(v);
            MarkCommandBuffersDirty();
        }
        public override void EnableDepthTest(bool v)
        {
            _state.SetDepthTestEnabled(v);
            MarkCommandBuffersDirty();
        }
        public override void DepthFunc(EComparison always)
        {
            _state.SetDepthCompare(ToVulkanCompareOp(always));
            MarkCommandBuffersDirty();
        }
        public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
        {
            if (program is null)
                return;

            uint x = (uint)Math.Max(numGroupsX, 1);
            uint y = (uint)Math.Max(numGroupsY, 1);
            uint z = (uint)Math.Max(numGroupsZ, 1);

            if (GetOrCreateAPIRenderObject(program) is not VkRenderProgram vkProgram)
            {
                Debug.VulkanWarning("DispatchCompute skipped: program could not be resolved to VkRenderProgram.");
                return;
            }

            vkProgram.Generate();
            if (!vkProgram.Link())
            {
                Debug.VulkanWarning($"DispatchCompute skipped: failed to link program '{program.Name ?? "UnnamedProgram"}'.");
                return;
            }

            int passIndex = EnsureValidPassIndex(Engine.Rendering.State.CurrentRenderGraphPassIndex, "DispatchCompute");
            EnqueueFrameOp(new ComputeDispatchOp(
                passIndex,
                vkProgram,
                x,
                y,
                z,
                vkProgram.CaptureComputeSnapshot(),
                CaptureFrameOpContext()));
        }
        public override void WaitForGpu()
        {
            DeviceWaitIdle();
        }
        public override void SetReadBuffer(EReadBufferMode mode)
        {
            _readBufferMode = mode;
        }
        public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        {
            _boundReadFrameBuffer = fbo;
            _readBufferMode = mode;

            if (fbo is not null)
            {
                EnsureFrameBufferRegistered(fbo);
                EnsureFrameBufferAttachmentsRegistered(fbo);

                if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is VkFrameBuffer vkFrameBuffer)
                    vkFrameBuffer.Generate();
            }
        }
        public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
        {
            switch (fboTarget)
            {
                case EFramebufferTarget.Framebuffer:
                    _boundReadFrameBuffer = fbo;
                    _boundDrawFrameBuffer = fbo;
                    break;
                case EFramebufferTarget.ReadFramebuffer:
                    _boundReadFrameBuffer = fbo;
                    break;
                case EFramebufferTarget.DrawFramebuffer:
                    _boundDrawFrameBuffer = fbo;
                    break;
                default:
                    return;
            }

            if (_boundDrawFrameBuffer is null)
                _state.SetCurrentTargetExtent(swapChainExtent);
            else
                _state.SetCurrentTargetExtent(new Extent2D(Math.Max(_boundDrawFrameBuffer.Width, 1u), Math.Max(_boundDrawFrameBuffer.Height, 1u)));

            if (fbo is not null)
            {
                EnsureFrameBufferRegistered(fbo);
                EnsureFrameBufferAttachmentsRegistered(fbo);

                if (GetOrCreateAPIRenderObject(fbo, generateNow: true) is VkFrameBuffer vkFrameBuffer)
                    vkFrameBuffer.Generate();
            }

            MarkCommandBuffersDirty();
        }
        public override void Clear(bool color, bool depth, bool stencil)
        {
            // Don't enqueue clear ops when there's no active rendering pipeline;
            // they would be emitted with an invalid pass index and dropped at recording time.
            if (Engine.Rendering.State.CurrentRenderingPipeline is null)
                return;

            _state.SetClearState(color, depth, stencil);

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = GetCurrentDrawFrameBuffer();
            Rect2D rect = _state.GetCroppingEnabled()
                ? _state.GetScissor()
                : new Rect2D(new Offset2D(0, 0), _state.GetCurrentTargetExtent());

            EnqueueFrameOp(new ClearOp(
                EnsureValidPassIndex(passIndex, "Clear"),
                target,
                color,
                depth,
                stencil,
                _state.GetClearColorValue(),
                _state.GetClearDepthValue(),
                _state.GetClearStencilValue(),
                rect,
                CaptureFrameOpContext()));
        }
        public override byte GetStencilIndex(float x, float y)
        {
            XRFrameBuffer? fbo = GetCurrentReadFrameBuffer() ?? GetCurrentDrawFrameBuffer();
            int sampleX;
            int sampleY;

            if (fbo is not null)
            {
                sampleX = Math.Clamp((int)x, 0, Math.Max((int)fbo.Width - 1, 0));
                sampleY = Math.Clamp((int)y, 0, Math.Max((int)fbo.Height - 1, 0));

                if (TryResolveBlitImage(
                        fbo,
                        _lastPresentedImageIndex,
                        GetReadBufferMode(),
                        wantColor: false,
                        wantDepth: false,
                        wantStencil: true,
                        out BlitImageInfo stencilSource,
                        isSource: true) &&
                    TryReadStencilPixel(stencilSource, sampleX, sampleY, out byte stencilValue))
                {
                    return stencilValue;
                }
            }

            if (_swapchainDepthImage.Handle == 0)
                return 0;

            sampleX = Math.Clamp((int)x, 0, Math.Max((int)swapChainExtent.Width - 1, 0));
            sampleY = Math.Clamp((int)y, 0, Math.Max((int)swapChainExtent.Height - 1, 0));

            BlitImageInfo swapchainStencilSource = ResolveSwapchainBlitImage(
                _lastPresentedImageIndex,
                wantColor: false,
                wantDepth: false,
                wantStencil: true);

            if (!swapchainStencilSource.IsValid)
                return 0;

            return TryReadStencilPixel(swapchainStencilSource, sampleX, sampleY, out byte swapchainStencil)
                ? swapchainStencil
                : (byte)0;
        }
        public override void SetCroppingEnabled(bool enabled)
        {
            _state.SetCroppingEnabled(enabled);
            MarkCommandBuffersDirty();
        }

        public void DeviceWaitIdle()
        {
            Result result = Api!.DeviceWaitIdle(device);
            if (result == Result.ErrorDeviceLost)
            {
                _deviceLost = true;
                Debug.VulkanWarning("[Vulkan] DeviceWaitIdle returned ErrorDeviceLost. Device state is irrecoverable.");
                // Don't throw — allow callers (e.g. RecreateSwapChain) to proceed with
                // teardown/recreation even after the device is lost, rather than getting
                // stuck in an infinite exception loop.
            }
        }

        public bool SupportsMultipleGraphicsQueues()
        {
            return HasSecondaryGraphicsQueue;
        }

        private static CompareOp ToVulkanCompareOp(EComparison comparison)
            => comparison switch
            {
                EComparison.Never => CompareOp.Never,
                EComparison.Less => CompareOp.Less,
                EComparison.Equal => CompareOp.Equal,
                EComparison.Lequal => CompareOp.LessOrEqual,
                EComparison.Greater => CompareOp.Greater,
                EComparison.Nequal => CompareOp.NotEqual,
                EComparison.Gequal => CompareOp.GreaterOrEqual,
                EComparison.Always => CompareOp.Always,
                _ => CompareOp.Always
            };
    }
}
