namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>The span of plan positions in which a logical resource is in use.</summary>
/// <param name="ResourceName">The logical resource name.</param>
/// <param name="FirstOrder">The plan position of the first pass that uses the resource.</param>
/// <param name="LastOrder">The plan position of the last pass that uses the resource.</param>
/// <param name="AliasGroup">The memory alias group of the resource, or -1 when it shares no memory.</param>
/// <param name="IsTransient">Whether the backend can discard the resource after its last use.</param>
public sealed record PlanResourceLifetimeSnapshot(
    string ResourceName,
    int FirstOrder,
    int LastOrder,
    int AliasGroup,
    bool IsTransient);
