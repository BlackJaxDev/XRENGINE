namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanCommandDiagnosticMarker
{
    public ulong Serial { get; init; }
    public string? OpKind { get; init; }
    public string? OutputTargetName { get; init; }
    public int PassIndex { get; init; }
    public int BatchIndex { get; init; }
    public int PipelineIdentity { get; init; }
    public int ViewportIdentity { get; init; }
    public ulong CommandBufferHandle { get; init; }
    public ulong CommandBufferRecordingGeneration { get; init; }
    public bool IsEmpty => Serial == 0;
}
