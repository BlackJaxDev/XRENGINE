namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrViewResourcePlannerContextKey(
        EOpenXrResourcePlannerPurpose Purpose,
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
        /// OpenXR swapchain image. The swapchain image/frame slot affects command recording, but the
        /// intermediate G-buffer, post-process, and depth resources are stable per eye/foveation
        /// configuration. Keeping image/frame indices out of this key prevents allocating a full
        /// offscreen render pipeline for every swapchain-image/frame-slot combination.
        /// </summary>
        public static OpenXrViewResourcePlannerContextKey FromTarget(in OpenXrEyeRenderTargetContext target)
            => new(
                EOpenXrResourcePlannerPurpose.Eye,
                target.ResourcePlannerStateIndex,
                target.OpenXrViewIndex,
                OpenXrExternalSwapchainTargetImageIndex,
                target.OpenXrViewIndex,
                target.OpenXrViewIndex,
                target.FoveationResourceKey,
                target.FoveationAttachmentKind,
                target.FoveationAttachmentOwnedByResourcePlanner);
    }
}
