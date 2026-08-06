namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrViewResourcePlannerContextKey(
    EVulkanOpenXrResourcePlannerPurpose Purpose,
    int ResourcePlannerStateIndex,
    uint OpenXrViewIndex,
    uint OpenXrImageIndex,
    uint CommandChainImageKey,
    uint FrameDataSlotIndex,
    ulong FoveationResourceKey,
    EVrFoveationAttachmentKind FoveationAttachmentKind,
    bool FoveationAttachmentOwnedByResourcePlanner)
{
    /// <summary>
    /// Builds the identity for resources described by the render graph, not for the acquired
    /// OpenXR swapchain image. Runtime image and frame-slot identity remain recording inputs.
    /// </summary>
    internal static VulkanOpenXrViewResourcePlannerContextKey FromTarget(
        in OpenXrEyeRenderTargetContext target)
        => new(
            EVulkanOpenXrResourcePlannerPurpose.Eye,
            target.ResourcePlannerStateIndex,
            target.OpenXrViewIndex,
            0,
            target.OpenXrViewIndex,
            target.OpenXrViewIndex,
            target.FoveationResourceKey,
            target.FoveationAttachmentKind,
            target.FoveationAttachmentOwnedByResourcePlanner);
}
