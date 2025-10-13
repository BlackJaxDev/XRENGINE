using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

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
        }

        public override void CleanUp()
        {
            DestroyAllSwapChainObjects();
            DestroyDescriptorSetLayout();

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
            throw new NotImplementedException();
        }
        public override void SetReadBuffer(EReadBufferMode mode)
        {
            throw new NotImplementedException();
        }
        public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        {
            throw new NotImplementedException();
        }
        public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
        {
            throw new NotImplementedException();
        }
        public override void Clear(bool color, bool depth, bool stencil)
        {
            _state.SetClearState(color, depth, stencil);
            MarkCommandBuffersDirty();
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
            => Api!.DeviceWaitIdle(device);

        public bool SupportsMultipleGraphicsQueues()
        {
            return false;
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