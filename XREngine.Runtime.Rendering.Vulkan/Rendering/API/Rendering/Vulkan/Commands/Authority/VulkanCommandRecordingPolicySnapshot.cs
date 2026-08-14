using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Recording policy sampled once by the frame loop. Primary recording must not
/// query mutable output, planner, or renderer configuration while encoding.
/// </summary>
internal readonly record struct VulkanCommandRecordingPolicySnapshot(
    bool UseDynamicRendering,
    bool AllowSynchronousResourceUploads,
    bool FreshSerialRecording,
    bool IsExternalSwapchainTarget,
    bool PreserveSwapchainForOverlay,
    bool TransitionSwapchainToPresent,
    bool PreferKhrDynamicRendering = false,
    ImageLayout FinalTargetLayout = ImageLayout.PresentSrcKhr);
