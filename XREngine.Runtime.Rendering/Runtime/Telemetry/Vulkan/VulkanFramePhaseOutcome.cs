namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable typed result published when a frame phase settles.</summary>
public readonly record struct VulkanFramePhaseOutcome(
    EVulkanFrameStage Stage,
    EVulkanFrameOutcome Outcome,
    EVulkanFrameWaitReason WaitReason);
