namespace XREngine.Components;

/// <summary>
/// Describes one authored tree within a flattened immutable chain template.
/// </summary>
public readonly record struct PhysicsChainTemplateTree(
    int ParticleStart,
    int ParticleCount,
    int MaximumDepth,
    float BoneTotalLength);
