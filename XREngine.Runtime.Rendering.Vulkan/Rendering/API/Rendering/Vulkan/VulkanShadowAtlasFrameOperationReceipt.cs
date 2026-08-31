namespace XREngine.Rendering.Vulkan;

/// <summary>Fixed-capacity upstream receipt for a directional-shadow atlas frame operation.</summary>
public readonly record struct VulkanShadowAtlasFrameOperationReceipt(
    EVulkanShadowAtlasFrameOperationReceiptStage Stage,
    string OperationKind,
    int PassIndex,
    int TargetIdentity,
    string TargetName);
