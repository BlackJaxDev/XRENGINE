namespace XREngine.Rendering.Resources;

public sealed record FrameBufferSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required,
    IReadOnlyList<FrameBufferAttachmentDescriptor> Attachments,
    Func<XRFrameBuffer>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.FrameBuffer,
        Lifetime,
        SizePolicy,
        Usage,
        Dependencies,
        Predicate,
        HistoryPolicy,
        DebugLabel,
        Required)
{
    public FrameBufferResourceDescriptor ToDescriptor()
        => new(Name, Lifetime, SizePolicy, Attachments);
}
