namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    internal static float ComputeDeterministicQualityPhase(int slot, uint generation)
    {
        uint hash = unchecked((uint)slot * 2654435761u) ^ unchecked(generation * 2246822519u);
        hash ^= hash >> 16;
        return (hash & 1023u) / 1024.0f;
    }

    internal static float ComputeCadenceProgress(float accumulatorSeconds, float rateHz, float fallback)
        => rateHz > 0.0f
            ? Math.Clamp(accumulatorSeconds * rateHz, 0.0f, 0.999999f)
            : Math.Clamp(fallback, 0.0f, 0.999999f);

    internal void AdvanceAutomaticQualityFrame()
    {
        if (_runtimeVisible)
            _offscreenQualityFrames = 0;
        else if (_offscreenQualityFrames < int.MaxValue)
            ++_offscreenQualityFrames;

        if (_recentInteractionQualityFramesRemaining > 0)
            --_recentInteractionQualityFramesRemaining;
    }

    internal PhysicsChainQualityTier ResolveAutomaticOffscreenTier(PhysicsChainQualityTier visibleTier)
    {
        if (_recentInteractionQualityFramesRemaining > 0)
            return PhysicsChainQualityTier.Strict;
        if (_runtimeVisible)
            return visibleTier;

        PhysicsChainOffscreenBehavior behavior = ResolveOffscreenBehavior(
            _offscreenBehavior,
            _automaticQualityImportance);
        return behavior switch
        {
            PhysicsChainOffscreenBehavior.Simulate => visibleTier,
            PhysicsChainOffscreenBehavior.SleepImmediately => PhysicsChainQualityTier.Sleep,
            _ => ResolveOffscreenDecayTier(visibleTier, _offscreenQualityFrames, _offscreenDecayFrameCount),
        };
    }

    internal static PhysicsChainOffscreenBehavior ResolveOffscreenBehavior(
        PhysicsChainOffscreenBehavior authored,
        int importance)
    {
        if (authored != PhysicsChainOffscreenBehavior.AutomaticByImportance)
            return authored;
        int normalizedImportance = Math.Clamp(importance, 0, 100);
        if (normalizedImportance >= 75)
            return PhysicsChainOffscreenBehavior.Simulate;
        return normalizedImportance >= 25
            ? PhysicsChainOffscreenBehavior.DecayThenSleep
            : PhysicsChainOffscreenBehavior.SleepImmediately;
    }

    internal static PhysicsChainQualityTier ResolveOffscreenDecayTier(
        PhysicsChainQualityTier visibleTier,
        int offscreenFrames,
        int decayFrames)
    {
        int normalizedDecayFrames = Math.Max(decayFrames, 1);
        int normalizedOffscreenFrames = Math.Max(offscreenFrames, 0);
        if (normalizedOffscreenFrames >= normalizedDecayFrames)
            return PhysicsChainQualityTier.Sleep;
        if ((long)normalizedOffscreenFrames * 3L >= (long)normalizedDecayFrames * 2L)
            return LowerCadence(visibleTier, PhysicsChainQualityTier.Hz7_5);
        if ((long)normalizedOffscreenFrames * 3L >= normalizedDecayFrames)
            return LowerCadence(visibleTier, PhysicsChainQualityTier.Hz15);
        return LowerCadence(visibleTier, PhysicsChainQualityTier.Hz30);
    }

    private static PhysicsChainQualityTier LowerCadence(
        PhysicsChainQualityTier current,
        PhysicsChainQualityTier requested)
        => GetCadenceRank(current) >= GetCadenceRank(requested) ? current : requested;

    private static int GetCadenceRank(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Strict => 0,
            PhysicsChainQualityTier.Hz30 => 1,
            PhysicsChainQualityTier.Hz15 => 2,
            PhysicsChainQualityTier.Hz7_5 => 3,
            PhysicsChainQualityTier.Sleep => 4,
            _ => 0,
        };

    private static bool IsIndependentQualityPolicyProperty(string? propertyName)
        => propertyName is nameof(SimulationPolicy)
            or nameof(CollisionPolicy)
            or nameof(PalettePolicy)
            or nameof(BoundsPolicy)
            or nameof(TransformMirrorPolicy);
}
