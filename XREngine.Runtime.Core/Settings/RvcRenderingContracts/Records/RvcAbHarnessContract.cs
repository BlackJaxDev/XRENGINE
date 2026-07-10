namespace XREngine;

public readonly record struct RvcAbHarnessContract(
    ERvcValidationScene Scene,
    RvcQualityToleranceSet Tolerances,
    bool ComparePerRegion,
    bool RequireSideBySideImages,
    bool RequireHumanReviewBeforeDefaultStereoReuse)
{
    public static RvcAbHarnessContract Default(ERvcValidationScene scene)
        => new(
            scene,
            RvcQualityToleranceSet.Default,
            ComparePerRegion: true,
            RequireSideBySideImages: true,
            RequireHumanReviewBeforeDefaultStereoReuse: true);
}
