namespace XREngine.Components;

/// <summary>Deterministic visible-tier selection from renderer relevance data.</summary>
public static class PhysicsChainAutomaticQualityEvaluation
{
    public static PhysicsChainQualityTier ResolveVisibleTier(
        in PhysicsChainAutomaticQualityObservation observation,
        int importance)
    {
        if (!observation.IsValid)
            return PhysicsChainQualityTier.Strict;

        return ResolveVisibleTierCore(observation.ProjectedSize, observation.Distance, importance);
    }

    /// <summary>Resolves a tier from validated scalar relevance values.</summary>
    [global::Engine.Diagnostics.HotPath]
    private static PhysicsChainQualityTier ResolveVisibleTierCore(
        float projectedSize,
        float distance,
        int importance)
    {
        PhysicsChainQualityTier tier = projectedSize switch
        {
            >= 0.10f => PhysicsChainQualityTier.Strict,
            >= 0.025f => PhysicsChainQualityTier.Hz30,
            >= 0.005f => PhysicsChainQualityTier.Hz15,
            _ => distance switch
            {
                <= 5.0f => PhysicsChainQualityTier.Strict,
                <= 25.0f => PhysicsChainQualityTier.Hz30,
                <= 100.0f => PhysicsChainQualityTier.Hz15,
                _ => PhysicsChainQualityTier.Hz7_5,
            },
        };

        int normalizedImportance = importance < 0 ? 0 : importance > 100 ? 100 : importance;
        if (normalizedImportance >= 75)
            return Promote(tier);
        return normalizedImportance < 25 ? Demote(tier) : tier;
    }

    /// <summary>Moves a tier one level toward strict simulation.</summary>
    [global::Engine.Diagnostics.HotPath]
    private static PhysicsChainQualityTier Promote(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Hz7_5 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Strict,
            _ => tier,
        };

    /// <summary>Moves a tier one level toward reduced simulation.</summary>
    [global::Engine.Diagnostics.HotPath]
    private static PhysicsChainQualityTier Demote(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Strict => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz7_5,
            _ => tier,
        };
}
