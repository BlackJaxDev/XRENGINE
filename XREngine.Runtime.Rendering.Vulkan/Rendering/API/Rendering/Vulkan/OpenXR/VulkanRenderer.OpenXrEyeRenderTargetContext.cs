using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrEyeRenderTargetContext(
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        Image Image,
        ImageView ImageView,
        Format ImageFormat,
        Extent2D Extent,
        Image DepthImage,
        DeviceMemory DepthMemory,
        ImageView DepthView,
        Format DepthFormat,
        ImageAspectFlags DepthAspect,
        BoundingRectangle ExternalTargetRegion,
        uint CommandChainImageKey,
        uint FrameDataSlotIndex,
        int ResourcePlannerStateIndex,
        ulong FoveationResourceKey,
        EVrFoveationAttachmentKind FoveationAttachmentKind,
        bool FoveationAttachmentOwnedByResourcePlanner)
    {
        public bool IsValid =>
            Image.Handle != 0 &&
            ImageView.Handle != 0 &&
            Extent.Width != 0 &&
            Extent.Height != 0 &&
            DepthImage.Handle != 0 &&
            DepthView.Handle != 0;
    }
}
