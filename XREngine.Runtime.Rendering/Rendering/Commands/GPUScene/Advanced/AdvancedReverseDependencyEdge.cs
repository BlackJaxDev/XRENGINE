namespace XREngine.Rendering.Commands;

/// <summary>Generation-safe source-to-dependent canonical identity edge.</summary>
public readonly record struct AdvancedReverseDependencyEdge(
    AdvancedGpuHandle Source,
    AdvancedGpuHandle Dependent);
