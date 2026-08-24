using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen desktop output, planner, and policy authority shared by the primary
/// reuse and fresh-recording paths for one acquired image.
/// </summary>
internal readonly record struct VulkanPreparedPrimaryAuthority(
    SwapchainRecordingTarget RecordingTarget,
    VulkanRecordedRenderTargetSnapshot RecordingTargetSnapshot,
    VulkanPresentationSourceTuple PresentationSource,
    VulkanPreparedResourcePlanStamp ResourcePlanStamp,
    VulkanCommandClearStateSnapshot ClearState,
    VulkanCommandRecordingPolicySnapshot Policy,
    ImageLayout TrackedTargetLayout);
