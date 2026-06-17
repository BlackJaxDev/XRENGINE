using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed record RenderBufferSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required,
    ERenderBufferStorage StorageFormat,
    uint Samples,
    EFrameBufferAttachment? DefaultAttachment,
    Func<XRRenderBuffer>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.RenderBuffer,
        Lifetime,
        SizePolicy,
        Usage,
        Dependencies,
        Predicate,
        HistoryPolicy,
        DebugLabel,
        Required)
{
    public RenderBufferResourceDescriptor ToDescriptor()
        => new(Name, Lifetime, SizePolicy, StorageFormat, Math.Max(1u, Samples), DefaultAttachment);
}
