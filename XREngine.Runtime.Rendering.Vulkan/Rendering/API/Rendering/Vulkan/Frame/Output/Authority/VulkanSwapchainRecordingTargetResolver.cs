using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the immutable attachment description used while recording a final-output command buffer.
/// Layout lookup remains a command-resource concern and is supplied by the caller so this output helper
/// does not retain a renderer or resource authority.
/// </summary>
internal static class VulkanSwapchainRecordingTargetResolver
{
    internal static SwapchainRecordingTarget Resolve(
        VulkanDesktopOutputState desktop,
        in VulkanSwapchainRecordingTargetInput input)
    {
        if (input.OpenXrTargetContext is { } openXrTarget && openXrTarget.IsValid)
        {
            return new SwapchainRecordingTarget(
                openXrTarget.Image,
                openXrTarget.ImageView,
                openXrTarget.ImageFormat,
                openXrTarget.Extent,
                openXrTarget.DepthImage,
                openXrTarget.DepthView,
                openXrTarget.DepthFormat,
                openXrTarget.DepthAspect,
                input.OpenXrInitialColorLayout,
                ImageEverPresentedAtRecordStart: false);
        }

        if (desktop.Images is null ||
            desktop.ImageViews is null ||
            input.ImageIndex >= desktop.Images.Length ||
            input.ImageIndex >= desktop.ImageViews.Length)
        {
            return default;
        }

        Image image = desktop.Images[input.ImageIndex];
        bool imageEverPresented = desktop.ImageEverPresented is not null &&
            input.ImageIndex < desktop.ImageEverPresented.Length &&
            desktop.ImageEverPresented[input.ImageIndex];
        ImageLayout initialColorLayout = input.DesktopInitialColorLayout;
        if (initialColorLayout == ImageLayout.Undefined && imageEverPresented)
            initialColorLayout = ImageLayout.PresentSrcKhr;

        return new SwapchainRecordingTarget(
            image,
            desktop.ImageViews[input.ImageIndex],
            desktop.ImageFormat,
            desktop.Extent,
            input.DepthResources?.Image ?? default,
            input.DepthResources?.View ?? default,
            input.DepthResources?.Format ?? default,
            input.DepthResources?.Aspect ?? default,
            initialColorLayout,
            imageEverPresented);
    }
}
