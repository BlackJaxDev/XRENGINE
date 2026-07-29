namespace XREngine.Rendering;

/// <summary>
/// Resolves shadow/probe/capture consumption without repeating deformation or
/// full material shading.
/// </summary>
public static class AdvancedSecondaryGeometryPolicyResolver
{
    public static AdvancedSecondaryGeometryPolicy Resolve(
        EAdvancedPreparationConsumer consumer,
        EAdvancedMaterialCoverageMode coverage,
        bool displacementChangesVisibility,
        bool compatiblePrimaryViewContract,
        bool requiresVelocity,
        bool requiresTemporalHistory)
    {
        bool isShadow =
            consumer is EAdvancedPreparationConsumer.DirectionalShadow or
                EAdvancedPreparationConsumer.PointShadow or
                EAdvancedPreparationConsumer.SpotShadow;
        bool isCapture =
            consumer is EAdvancedPreparationConsumer.Probe or
                EAdvancedPreparationConsumer.Capture;
        if (!isShadow && !isCapture)
            throw new ArgumentOutOfRangeException(nameof(consumer));

        EAdvancedCapturePreviousDataPolicy previousPolicy =
            requiresVelocity
                ? EAdvancedCapturePreviousDataPolicy.RequiredForVelocity
                : requiresTemporalHistory
                    ? EAdvancedCapturePreviousDataPolicy.RequiredForTemporalHistory
                    : EAdvancedCapturePreviousDataPolicy.NotRequired;

        return new AdvancedSecondaryGeometryPolicy(
            consumer,
            ReuseAggregateDeformation: true,
            ReusePrimaryRelevance: compatiblePrimaryViewContract,
            RequiresIndependentFrustum: !compatiblePrimaryViewContract ||
                isShadow,
            EvaluateCoverageMaterial:
                coverage == EAdvancedMaterialCoverageMode.Masked,
            EvaluateDisplacementMaterial: displacementChangesVisibility,
            PreviousDataPolicy: previousPolicy);
    }
}
