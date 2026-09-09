namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable meaning for a GPU checkpoint token.</summary>
internal readonly record struct VulkanNvCheckpointMarkerIdentity(
    string OpKind,
    string? ProgramName,
    EVulkanNvCheckpointPhase Phase,
    string? OutputTargetName,
    int PassIndex,
    int BatchIndex,
    int PipelineIdentity,
    int ViewportIdentity);
