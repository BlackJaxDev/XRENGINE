namespace XREngine;

public readonly record struct RenderOutputCompatibilityProof(
    ERenderSharedInput SharedInputs,
    ulong SceneRevision,
    ulong MaterialGeneration,
    ulong ShadowGeneration,
    ulong ResourceGeneration,
    bool ViewIdentityMatches,
    bool ProjectionMatches,
    bool DepthConventionMatches)
{
    private const ERenderSharedInput ViewDependentInputs =
        ERenderSharedInput.Visibility | ERenderSharedInput.HiZ | ERenderSharedInput.ViewConstants;

    public bool IsValid
        => (SharedInputs & ViewDependentInputs) == 0
            || (ViewIdentityMatches && ProjectionMatches && DepthConventionMatches);
}
