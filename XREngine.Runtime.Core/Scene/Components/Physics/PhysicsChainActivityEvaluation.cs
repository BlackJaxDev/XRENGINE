namespace XREngine.Components;

/// <summary>Deterministic activity-error and sleep-hysteresis evaluation.</summary>
public static class PhysicsChainActivityEvaluation
{
    public static float ComputeNormalizedErrorSquared(
        in PhysicsChainActivitySignals signals,
        in PhysicsChainActivityThresholds thresholds)
    {
        PhysicsChainActivityThresholds normalized = thresholds.Normalized;
        float error = MathF.Max(
            NormalizeSquared(signals.MaximumParticleVelocitySquared, normalized.ParticleVelocity),
            NormalizeSquared(signals.MaximumConstraintErrorSquared, normalized.ConstraintError));
        error = MathF.Max(error, NormalizeSquared(signals.RootAccelerationSquared, normalized.RootAcceleration));
        error = MathF.Max(error, NormalizeSquared(signals.ExternalForceMagnitudeSquared, normalized.ExternalForce));
        if (signals.ColliderChanged || signals.RecentlyVisibleOrUsed)
            error = MathF.Max(error, normalized.WakeMultiplier * normalized.WakeMultiplier);
        return float.IsFinite(error) ? error : float.PositiveInfinity;
    }

    public static bool IsQuiet(float normalizedErrorSquared)
        => normalizedErrorSquared <= 1.0f;

    public static bool ShouldWake(float normalizedErrorSquared, float wakeMultiplier)
    {
        float normalizedMultiplier = MathF.Max(wakeMultiplier, 1.01f);
        return normalizedErrorSquared >= normalizedMultiplier * normalizedMultiplier;
    }

    private static float NormalizeSquared(float magnitudeSquared, float threshold)
        => MathF.Max(magnitudeSquared, 0.0f) / (threshold * threshold);
}
