namespace XREngine.Components;

/// <summary>
/// Last CPU activity sample used by automatic sleep evaluation.
/// Squared values avoid square roots in the update path.
/// </summary>
public readonly record struct PhysicsChainActivitySnapshot(
    float MaximumParticleDisplacementSquared,
    float MaximumConstraintErrorSquared,
    float RootDisplacementSquared,
    float RootAccelerationSquared,
    float ExternalForceMagnitudeSquared,
    float NormalizedErrorSquared,
    int QuietSimulationFrames,
    bool IsQuiet,
    bool ColliderChanged,
    bool RecentlyVisibleOrUsed);
