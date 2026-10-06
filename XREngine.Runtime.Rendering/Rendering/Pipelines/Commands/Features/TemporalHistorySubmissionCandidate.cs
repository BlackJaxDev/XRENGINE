namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Transfers one temporal attempt to the backend that records and submits it.</summary>
internal readonly record struct TemporalHistorySubmissionCandidate(object? Owner, ulong Epoch, ulong Token)
{
    public bool IsValid => Owner is not null && Epoch != 0 && Token != 0;
    public XRRenderPipelineInstance? PipelineInstance { get; init; }
    public ulong RenderFrameId { get; init; }
    public ulong ProfileGeneration { get; init; }
    public ulong LeftEyeResetGeneration { get; init; }
    public ulong RightEyeResetGeneration { get; init; }
    public uint ExpectedLayerMask { get; init; }
    public uint CompleteLayerMask { get; init; }
    public bool AuthoredHistoryReady { get; init; }
    public bool RequiresTsrColor { get; init; }
    public XRTexture? ColorReadTexture { get; init; }
    public XRTexture? DepthReadTexture { get; init; }
    public XRTexture? TsrColorReadTexture { get; init; }
    public XRTexture? MetadataReadTexture { get; init; }
    public XRTexture? ExposureReadTexture { get; init; }
    public XRFrameBuffer? ColorHistoryTarget { get; init; }
    public XRFrameBuffer? DepthHistoryTarget { get; init; }
    public XRFrameBuffer? TsrColorHistoryTarget { get; init; }
    public XRFrameBuffer? MetadataHistoryTarget { get; init; }
    public XRFrameBuffer? ExposureHistoryTarget { get; init; }
    public XRFrameBuffer? TemporalResolveTarget { get; init; }
    public XRFrameBuffer? TsrResolveTarget { get; init; }
    public TemporalHistoryResourceSet ReadResources { get; init; }
    public TemporalHistoryResourceSet WriteResources { get; init; }
}
