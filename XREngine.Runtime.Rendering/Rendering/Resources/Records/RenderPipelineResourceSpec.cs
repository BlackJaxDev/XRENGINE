namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a render pipeline resource, including its name, kind, lifetime, size policy, usage, dependencies, predicate, history policy, debug label, and whether it is required.
/// </summary>
/// <param name="Name">The name of the render pipeline resource.</param>
/// <param name="Kind">The kind of the render pipeline resource.</param>
/// <param name="Lifetime">The lifetime of the render pipeline resource.</param>
/// <param name="SizePolicy">The size policy of the render pipeline resource.</param>
/// <param name="Usage">The usage of the render pipeline resource.</param>
/// <param name="Dependencies">The dependencies of the render pipeline resource.</param>
/// <param name="Predicate">The predicate for the render pipeline resource.</param>
/// <param name="HistoryPolicy">The history policy of the render pipeline resource.</param>
/// <param name="DebugLabel">The debug label of the render pipeline resource.</param>
/// <param name="Required">Indicates whether the render pipeline resource is required.</param>
public abstract record RenderPipelineResourceSpec(
    string Name,
    RenderPipelineResourceKind Kind,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required)
{
    /// <summary>
    /// Determines whether the render pipeline resource is enabled based on the given profile.
    /// </summary>
    /// <param name="profile">The render pipeline resource profile.</param>
    /// <returns>True if the resource is enabled; otherwise, false.</returns>
    public bool IsEnabled(RenderPipelineResourceProfile profile)
        => Predicate?.Invoke(profile) ?? true;
}
