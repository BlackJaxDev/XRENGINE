namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanNvCheckpointMarker
{
    public ulong Serial { get; init; }
    /// <summary>Frame that first recorded this immutable checkpoint identity.</summary>
    public ulong FirstRecordedFrameId { get; init; }
    public string? OpKind { get; init; }
    public string? ProgramName { get; init; }
    public EVulkanNvCheckpointPhase Phase { get; init; }
    public string? OutputTargetName { get; init; }
    public int PassIndex { get; init; }
    public int BatchIndex { get; init; }
    public int PipelineIdentity { get; init; }
    public int ViewportIdentity { get; init; }
    public ulong FirstCommandBufferHandle { get; init; }
    public ulong FirstCommandBufferRecordingGeneration { get; init; }
}
