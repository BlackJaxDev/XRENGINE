namespace XREngine.Rendering.Vulkan.Commands;

/// <summary>Immutable diagnostic snapshot of one queued Vulkan frame operation.</summary>
internal sealed record VulkanFrameOpTraceEntry(
    int Index,
    string OpType,
    int PassIndex,
    string PassName,
    string TargetName,
    int TargetIdentity,
    int PipelineIdentity,
    string PipelineName,
    int ViewportIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    string Detail);