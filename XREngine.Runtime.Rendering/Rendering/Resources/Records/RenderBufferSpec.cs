using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a render buffer resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the render buffer resource.</param>
/// <param name="Lifetime">The lifetime of the render buffer resource.</param>
/// <param name="SizePolicy">The size policy of the render buffer resource.</param>
/// <param name="Usage">The usage of the render buffer resource.</param>
/// <param name="Dependencies">The dependencies of the render buffer resource.</param>
/// <param name="Predicate">The predicate for the render buffer resource.</param>
/// <param name="HistoryPolicy">The history policy of the render buffer resource.</param>
/// <param name="DebugLabel">The debug label of the render buffer resource.</param>
/// <param name="Required">Indicates whether the render buffer resource is required.</param>
/// <param name="StorageFormat">The storage format of the render buffer resource.</param>
/// <param name="Samples">The number of samples for the render buffer resource.</param>
/// <param name="DefaultAttachment">The default attachment for the render buffer resource.</param>
/// <param name="Factory">The factory function for creating the render buffer resource.</param>
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
    /// <summary>
    /// Converts this render buffer specification into a descriptor that can be used to create or manage the actual render buffer resource.
    /// </summary>
    /// <returns>A descriptor representing the render buffer resource.</returns>
    public RenderBufferResourceDescriptor ToDescriptor()
        => new(Name, Lifetime, SizePolicy, StorageFormat, Math.Max(1u, Samples), DefaultAttachment);
}
