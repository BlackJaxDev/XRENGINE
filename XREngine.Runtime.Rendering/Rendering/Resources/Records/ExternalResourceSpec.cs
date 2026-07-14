namespace XREngine.Rendering.Resources;

/// <summary>
/// Declares a resource consumed by a pipeline generation but owned and bound by
/// an external system. External resources are never materialized or destroyed
/// by <see cref="RenderPipelineResourceManager"/>.
/// </summary>
public sealed record ExternalResourceSpec(
    string Name,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    string? DebugLabel,
    ExternalRenderResourceKind ExternalKind,
    ExternalRenderResourceOwnership Ownership,
    ExternalRenderResourceSynchronization Synchronization)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.External,
        RenderResourceLifetime.External,
        RenderResourceSizePolicy.Absolute(0u, 0u),
        RenderPipelineResourceUsage.None,
        Dependencies,
        Predicate,
        RenderResourceHistoryPolicy.None,
        DebugLabel,
        Required: false);
