using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal readonly record struct SwapchainRecordingTarget(
        Image Image,
        ImageView ImageView,
        Format ImageFormat,
        Extent2D Extent,
        Image DepthImage,
        ImageView DepthView,
        Format DepthFormat,
        ImageAspectFlags DepthAspect,
        ImageLayout InitialColorLayout,
        bool ImageEverPresentedAtRecordStart,
        RenderPass RenderPass = default,
        RenderPass LoadRenderPass = default,
        Framebuffer Framebuffer = default)
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
