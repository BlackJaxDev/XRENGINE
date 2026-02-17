using Silk.NET.Vulkan;
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
        }

        public override void CleanUp()
        {
            if (device.Handle != 0)
                DeviceWaitIdle();

            DestroyAutoExposureComputeResources();
            DisposeImGuiResources();
            DestroyAllSwapChainObjects();
            DestroyDescriptorSetLayout();
            _resourceAllocator.DestroyPhysicalImages(this);
            _resourceAllocator.DestroyPhysicalBuffers(this);
            _stagingManager.Destroy(this);
            DestroyFrameTimingResources();

            DestroySyncObjects();
            DestroyCommandPool();

            DestroyLogicalDevice();
            DestroyValidationLayers();
            DestroySurface();
            DestroyInstance();
        }

        // It should be noted that in a real world application, you're not supposed to actually call vkAllocateMemory for every individual buffer.
        // The maximum number of simultaneous memory allocations is limited by the maxMemoryAllocationCount physical device limit, which may be as low as 4096 even on high end hardware like an NVIDIA GTX 1080.
        // The right way to allocate memory for a large number of objects at the same time is to create a custom allocator that splits up a single allocation among many different objects by using the offset parameters that we've seen in many functions.

        private void AllocateMemory(MemoryAllocateInfo allocInfo, DeviceMemory* memPtr)
        {
            AllocationCallbacks callbacks = new()
            {
                PfnAllocation = new PfnAllocationFunction(Allocated),
                PfnReallocation = new PfnReallocationFunction(Reallocated),
                PfnFree = new PfnFreeFunction(Freed),
                PfnInternalAllocation = new PfnInternalAllocationNotification(InternalAllocated),
                PfnInternalFree = new PfnInternalFreeNotification(InternalFreed)
            };
            if (Api!.AllocateMemory(device, ref allocInfo, null, memPtr) != Result.Success)
                throw new Exception("Failed to allocate memory.");
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
            _state.SetClearState(color, depth, stencil);

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            XRFrameBuffer? target = _boundDrawFrameBuffer;
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
            throw new NotImplementedException();
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
