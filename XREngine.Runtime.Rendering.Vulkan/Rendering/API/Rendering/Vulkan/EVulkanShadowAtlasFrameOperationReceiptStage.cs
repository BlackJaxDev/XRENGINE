namespace XREngine.Rendering.Vulkan;

/// <summary>Scheduling boundary that observed a directional-shadow atlas operation.</summary>
public enum EVulkanShadowAtlasFrameOperationReceiptStage
{
    Enqueued,
    PrimaryAdmission,
    PrimaryDeferredByPlan,
}
