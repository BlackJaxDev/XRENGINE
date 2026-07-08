namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a framebuffer resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the framebuffer resource.</param>
/// <param name="Lifetime">The lifetime of the framebuffer resource.</param>
/// <param name="SizePolicy">The size policy of the framebuffer resource.</param>
/// <param name="Usage">The usage of the framebuffer resource.</param>
/// <param name="Dependencies">The dependencies of the framebuffer resource.</param>
/// <param name="Predicate">The predicate for the framebuffer resource.</param>
/// <param name="HistoryPolicy">The history policy of the framebuffer resource.</param>
/// <param name="DebugLabel">The debug label of the framebuffer resource.</param>
/// <param name="Required">Indicates whether the framebuffer resource is required.</param>
/// <param name="Attachments">The attachments of the framebuffer resource.</param>
/// <param name="Factory">The factory function for creating the framebuffer resource.</param>
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
    /// <summary>
    /// Converts this framebuffer specification into a descriptor that can be used to create or manage the actual framebuffer resource.
    /// </summary>
    /// <returns>A descriptor representing the framebuffer resource.</returns>
    public FrameBufferResourceDescriptor ToDescriptor()
        => new(Name, Lifetime, SizePolicy, Attachments);
}
