using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImGuiPlatformSwapchainGeneration(
    SwapchainKHR Swapchain,
    Format Format,
    ColorSpaceKHR ColorSpace,
    Extent2D Extent,
    Image[] Images,
    ImageView[] ImageViews);
