namespace XREngine.Rendering.Vulkan;

/// <summary>Native attachment receipt for one directional shadow-atlas writer scope.</summary>
public readonly record struct VulkanShadowAtlasWriterReceipt(
    string FramebufferName,
    ulong ImageHandle,
    ulong ImageGeneration,
    ulong ImageViewHandle,
    uint BaseMipLevel,
    uint LevelCount,
    uint BaseArrayLayer,
    uint LayerCount,
    bool HasExecutedDepthClear,
    float ExecutedClearDepth,
    int ClearOffsetX,
    int ClearOffsetY,
    uint ClearWidth,
    uint ClearHeight,
    string ScopeLoadOp,
    string ScopeStoreOp);
