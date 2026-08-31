namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Optional allocation attribution for one explicit-target frame. The probe is
/// disabled by default and is intended for deterministic harness diagnosis.
/// </summary>
public readonly record struct VulkanExplicitTargetFrameAllocationCounters(
    long AcquireFrameTarget,
    long BeginFrameRecording,
    long BeginTrackedCommandBuffer,
    long BeginFrameResourceTracking,
    long BeginBindStateInitialization,
    long BeginTrackingInitialization,
    long BeginNativeCommandBuffer,
    long RecordCallback,
    long EndFrameRecording,
    long QueueSubmission,
    long CompleteFrameTarget);
