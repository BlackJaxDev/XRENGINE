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

    /// <summary>Tests the normalized activity error against the sleep threshold.</summary>
    [global::Engine.Diagnostics.HotPath]
    public static bool IsQuiet(float normalizedErrorSquared)
        => normalizedErrorSquared <= 1.0f;

    /// <summary>Tests activity against the wake threshold.</summary>
    [global::Engine.Diagnostics.HotPath]
    public static bool ShouldWake(float normalizedErrorSquared, float wakeMultiplier)
    {
        float normalizedMultiplier = wakeMultiplier <= 1.01f ? 1.01f : wakeMultiplier;
        return normalizedErrorSquared >= normalizedMultiplier * normalizedMultiplier;
    }

    /// <summary>Scales one squared activity channel by its quiet threshold.</summary>
    [global::Engine.Diagnostics.HotPath]
    private static float NormalizeSquared(float magnitudeSquared, float threshold)
        => (magnitudeSquared <= 0.0f ? 0.0f : magnitudeSquared) / (threshold * threshold);
}
