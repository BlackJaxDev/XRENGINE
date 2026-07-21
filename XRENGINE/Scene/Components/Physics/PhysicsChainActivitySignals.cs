namespace XREngine.Components;

/// <summary>
/// Backend-neutral activity inputs. Squared magnitudes keep the per-frame
/// evaluation branch-only and avoid square roots.
/// </summary>
public readonly record struct PhysicsChainActivitySignals(
    float MaximumParticleVelocitySquared,
    float MaximumConstraintErrorSquared,
    float RootAccelerationSquared,
    float ExternalForceMagnitudeSquared,
    bool ColliderChanged,
    bool RecentlyVisibleOrUsed);
