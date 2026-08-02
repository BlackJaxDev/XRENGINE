namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stack-only capture of the cache/scheduling inputs that must remain stable for
/// one command-buffer lookup and potential recording operation.
/// ref struct is used to ensure that this context is not accidentally captured 
/// by a lambda or async method, which would extend its lifetime beyond the intended scope.
/// </summary>
internal ref struct VulkanCommandSchedulingContext<TVariant>(
    uint imageIndex,
    bool preserveSwapchainForOverlay,
    VulkanRenderGraphPlan renderGraphPlan)
    where TVariant : class
{
    public uint ImageIndex { get; } = imageIndex;
    public bool PreserveSwapchainForOverlay { get; } = preserveSwapchainForOverlay;
    public VulkanRenderGraphPlan RenderGraphPlan { get; } = renderGraphPlan;

    public string RecordingDeferredReason = string.Empty;
    public Silk.NET.Vulkan.CommandBuffer DynamicUiSecondaryCommandBuffer = default;
    public int DynamicUiOverlayOperationCount = 0;
    public FrameOp[] DynamicUiOverlayOperations = Array.Empty<FrameOp>();
    public ulong DynamicUiOverlaySignature = 0;
    public TVariant? DynamicUiOverlayVariant = null;
    public Silk.NET.Vulkan.CommandBuffer TextureUploadCommandBuffer = default;
    public Silk.NET.Vulkan.CommandPool TextureUploadCommandPool = default;
    public Silk.NET.Vulkan.ImageLayout SwapchainLayoutAfterCommandBuffer = Silk.NET.Vulkan.ImageLayout.PresentSrcKhr;
    public long CommandBufferDirtyGenerationAfterRecord = 0;
}
