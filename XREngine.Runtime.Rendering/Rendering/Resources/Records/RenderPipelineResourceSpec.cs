namespace XREngine.Rendering.Resources;

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
    public bool IsEnabled(RenderPipelineResourceProfile profile)
        => Predicate?.Invoke(profile) ?? true;
}
