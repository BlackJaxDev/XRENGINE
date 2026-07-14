namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a quad material resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the quad material resource.</param>
/// <param name="Lifetime">The lifetime of the quad material resource.</param>
/// <param name="Dependencies">The dependencies of the quad material resource.</param>
/// <param name="Predicate">The predicate for the quad material resource.</param>
/// <param name="DebugLabel">The debug label of the quad material resource.</param>
/// <param name="Required">Indicates whether the quad material resource is required.</param>
/// <param name="Factory">Creates the fullscreen-quad framebuffer helper.</param>
public sealed record QuadMaterialSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    string? DebugLabel,
    bool Required,
    Func<XRFrameBuffer>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.QuadMaterial,
        Lifetime,
        RenderResourceSizePolicy.Absolute(0u, 0u),
        RenderPipelineResourceUsage.SampledTexture,
        Dependencies,
        Predicate,
        RenderResourceHistoryPolicy.None,
        DebugLabel,
        Required);
