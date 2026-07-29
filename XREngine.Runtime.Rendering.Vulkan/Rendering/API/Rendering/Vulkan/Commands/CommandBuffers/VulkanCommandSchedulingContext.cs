namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stack-only capture of the cache/scheduling inputs that must remain stable for
/// one command-buffer lookup and potential recording operation.
/// </summary>
internal ref struct VulkanCommandSchedulingContext<TVariant>
    where TVariant : class
{
    public VulkanCommandSchedulingContext(
        uint imageIndex,
        bool preserveSwapchainForOverlay,
        VulkanRenderGraphPlan renderGraphPlan)
    {
        ImageIndex = imageIndex;
        PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
        RenderGraphPlan = renderGraphPlan;
        RecordingDeferredReason = string.Empty;
        DynamicUiSecondaryCommandBuffer = default;
        DynamicUiOverlayOperationCount = 0;
        DynamicUiOverlayOperations = Array.Empty<VulkanRenderer.FrameOp>();
        DynamicUiOverlaySignature = 0;
        DynamicUiOverlayVariant = null;
        TextureUploadCommandBuffer = default;
        TextureUploadCommandPool = default;
        SwapchainLayoutAfterCommandBuffer = Silk.NET.Vulkan.ImageLayout.PresentSrcKhr;
        CommandBufferDirtyGenerationAfterRecord = 0;
    }

    public uint ImageIndex { get; }
    public bool PreserveSwapchainForOverlay { get; }
    public VulkanRenderGraphPlan RenderGraphPlan { get; }

    public string RecordingDeferredReason;
    public Silk.NET.Vulkan.CommandBuffer DynamicUiSecondaryCommandBuffer;
    public int DynamicUiOverlayOperationCount;
    public VulkanRenderer.FrameOp[] DynamicUiOverlayOperations;
    public ulong DynamicUiOverlaySignature;
    public TVariant? DynamicUiOverlayVariant;
    public Silk.NET.Vulkan.CommandBuffer TextureUploadCommandBuffer;
    public Silk.NET.Vulkan.CommandPool TextureUploadCommandPool;
    public Silk.NET.Vulkan.ImageLayout SwapchainLayoutAfterCommandBuffer;
    public long CommandBufferDirtyGenerationAfterRecord;
}
