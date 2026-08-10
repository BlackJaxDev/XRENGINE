using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen command-resource observations consumed by final-output target selection.
/// The command authority computes layouts before entering output code.
/// </summary>
internal readonly record struct VulkanSwapchainRecordingTargetInput(
    uint ImageIndex,
    OpenXrEyeRenderTargetContext? OpenXrTargetContext,
    VulkanSwapchainDepthResources? DepthResources,
    ImageLayout OpenXrInitialColorLayout,
    ImageLayout DesktopInitialColorLayout);
