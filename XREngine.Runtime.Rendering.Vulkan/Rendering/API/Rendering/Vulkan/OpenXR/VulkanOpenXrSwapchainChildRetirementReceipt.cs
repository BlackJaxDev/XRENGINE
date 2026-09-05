using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native children detached with an OpenXR swapchain generation. The
/// retirement queues own destruction; this receipt only proves that it happened.
/// </summary>
internal readonly record struct VulkanOpenXrSwapchainChildRetirementReceipt(
    ImageView[] ImageViews,
    Framebuffer[] Framebuffers,
    VulkanPinnedResourceGeneration[] ResourceGenerations,
    bool IsValid)
{
    public static VulkanOpenXrSwapchainChildRetirementReceipt Empty { get; } =
        new([], [], [], true);
}
