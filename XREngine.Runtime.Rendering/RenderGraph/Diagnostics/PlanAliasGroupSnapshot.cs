namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>Resources whose lifetimes do not overlap, so a backend places them in the same memory.</summary>
/// <param name="GroupIndex">The alias group index.</param>
/// <param name="ResourceNames">The logical names of the resources in the group.</param>
/// <param name="Description">The backend description of the group, such as its size and memory type.</param>
public sealed record PlanAliasGroupSnapshot(
    int GroupIndex,
    IReadOnlyList<string> ResourceNames,
    string Description);
