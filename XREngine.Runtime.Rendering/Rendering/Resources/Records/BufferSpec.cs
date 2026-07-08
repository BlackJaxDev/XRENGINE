using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a buffer resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the buffer resource.</param>
/// <param name="Lifetime">The lifetime of the buffer resource.</param>
/// <param name="SizePolicy">The size policy of the buffer resource.</param>
/// <param name="Usage">The usage of the buffer resource.</param>
/// <param name="Dependencies">The dependencies of the buffer resource.</param>
/// <param name="Predicate">The predicate for the buffer resource.</param>
/// <param name="HistoryPolicy">The history policy of the buffer resource.</param>
/// <param name="DebugLabel">The debug label of the buffer resource.</param>
/// <param name="Required">Indicates whether the buffer resource is required.</param>
/// <param name="SizeInBytes">The size of the buffer resource in bytes.</param>
/// <param name="Target">The target of the buffer resource.</param>
/// <param name="BufferUsage">The usage of the buffer resource.</param>
/// <param name="ElementStride">The stride of each element in the buffer resource.</param>
/// <param name="ElementCount">The number of elements in the buffer resource.</param>
/// <param name="AccessPattern">The access pattern of the buffer resource.</param>
/// <param name="Factory">The factory function for creating the buffer resource.</param>
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
    /// <summary>
    /// Converts this buffer specification into a descriptor that can be used to create or manage the actual buffer resource.
    /// </summary>
    /// <returns>A descriptor representing the buffer resource.</returns>
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
