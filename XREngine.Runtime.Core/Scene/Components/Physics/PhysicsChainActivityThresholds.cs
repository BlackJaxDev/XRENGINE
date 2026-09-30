namespace XREngine.Components;

/// <summary>Per-channel quiet thresholds plus a wake/enter hysteresis ratio.</summary>
public readonly record struct PhysicsChainActivityThresholds(
    float ParticleVelocity,
    float ConstraintError,
    float RootAcceleration,
    float ExternalForce,
    float WakeMultiplier)
{
    public PhysicsChainActivityThresholds Normalized
        => new(
            MathF.Max(ParticleVelocity, 1e-7f),
            MathF.Max(ConstraintError, 1e-7f),
            MathF.Max(RootAcceleration, 1e-7f),
            MathF.Max(ExternalForce, 1e-7f),
            MathF.Max(WakeMultiplier, 1.01f));
}
