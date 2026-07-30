namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// One contiguous run of compatible operations eligible for secondary recording.
/// </summary>
internal readonly record struct VulkanSecondaryRecordingBucket(
    int StartIndex,
    int Count,
    int PassIndex,
    int TargetIdentity,
    int SchedulingIdentity,
    Type OpType,
    FrameOpContext Context);
