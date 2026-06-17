using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed record BufferSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required,
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage BufferUsage,
    uint ElementStride,
    uint ElementCount,
    EBufferAccessPattern AccessPattern,
    Func<XRDataBuffer>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.Buffer,
        Lifetime,
        SizePolicy,
        Usage,
        Dependencies,
        Predicate,
        HistoryPolicy,
        DebugLabel,
        Required)
{
    public BufferResourceDescriptor ToDescriptor()
        => new(
            Name,
            Lifetime,
            Math.Max(1UL, SizeInBytes),
            Target,
            BufferUsage,
            SupportsAliasing: Lifetime == RenderResourceLifetime.Transient,
            ElementStride,
            ElementCount,
            AccessPattern);
}
