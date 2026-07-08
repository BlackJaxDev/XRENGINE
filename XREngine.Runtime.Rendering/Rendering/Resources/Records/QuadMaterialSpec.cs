namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a quad material resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the quad material resource.</param>
/// <param name="Lifetime">The lifetime of the quad material resource.</param>
/// <param name="SizePolicy">The size policy of the quad material resource.</param>
/// <param name="Usage">The usage of the quad material resource.</param>
/// <param name="Dependencies">The dependencies of the quad material resource.</param>
/// <param name="Predicate">The predicate for the quad material resource.</param>
/// <param name="HistoryPolicy">The history policy of the quad material resource.</param>
/// <param name="DebugLabel">The debug label of the quad material resource.</param>
/// <param name="Required">Indicates whether the quad material resource is required.</param>
public sealed record QuadMaterialSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.QuadMaterial,
        Lifetime,
        SizePolicy,
        Usage,
        Dependencies,
        Predicate,
        HistoryPolicy,
        DebugLabel,
        Required);
