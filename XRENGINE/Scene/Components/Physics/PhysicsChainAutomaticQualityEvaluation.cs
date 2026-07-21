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

        PhysicsChainQualityTier tier = observation.ProjectedSize switch
        {
            >= 0.10f => PhysicsChainQualityTier.Strict,
            >= 0.025f => PhysicsChainQualityTier.Hz30,
            >= 0.005f => PhysicsChainQualityTier.Hz15,
            _ => observation.Distance switch
            {
                <= 5.0f => PhysicsChainQualityTier.Strict,
                <= 25.0f => PhysicsChainQualityTier.Hz30,
                <= 100.0f => PhysicsChainQualityTier.Hz15,
                _ => PhysicsChainQualityTier.Hz7_5,
            },
        };

        int normalizedImportance = Math.Clamp(importance, 0, 100);
        if (normalizedImportance >= 75)
            return Promote(tier);
        return normalizedImportance < 25 ? Demote(tier) : tier;
    }

    private static PhysicsChainQualityTier Promote(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Hz7_5 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Strict,
            _ => tier,
        };

    private static PhysicsChainQualityTier Demote(PhysicsChainQualityTier tier)
        => tier switch
        {
            PhysicsChainQualityTier.Strict => PhysicsChainQualityTier.Hz30,
            PhysicsChainQualityTier.Hz30 => PhysicsChainQualityTier.Hz15,
            PhysicsChainQualityTier.Hz15 => PhysicsChainQualityTier.Hz7_5,
            _ => tier,
        };
}
