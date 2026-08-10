using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen final-output attachments consumed by the dynamic UI text overlay
/// recorder. This deliberately contains native values rather than an output
/// authority reference so recording cannot observe a replacement generation.
/// </summary>
internal readonly record struct VulkanDynamicUiOverlayTarget(
    Image SwapchainImage,
    ImageView SwapchainView,
    Extent2D Extent,
    bool HasStreamlineUi,
    Image StreamlineUiImage,
    ImageView StreamlineUiView,
    ImageLayout StreamlineUiInitialLayout);
