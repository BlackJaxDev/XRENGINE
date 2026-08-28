using XREngine.Animation.Importers;

namespace XREngine.Animation;

/// <summary>
/// Immutable state-machine leaf sample retained alongside the ordinary pose
/// value store until the target-avatar evaluator can project Unity Body motion.
/// </summary>
public readonly record struct HumanoidMotionContribution(
    AnimationClip Clip,
    ImportedHumanoidRootMotionPolicy Policy,
    ulong OccurrenceId,
    ulong LifecycleGeneration,
    float Weight,
    float SampleTime,
    float SamplePhase,
    long PlaybackLoopCycle,
    long SourceLoopCycle,
    EHumanoidMotionContributionType ContributionType)
{
    public HumanoidMotionContribution WithWeightAndType(
        float weight,
        EHumanoidMotionContributionType contributionType)
        => new(
            Clip,
            Policy,
            OccurrenceId,
            LifecycleGeneration,
            weight,
            SampleTime,
            SamplePhase,
            PlaybackLoopCycle,
            SourceLoopCycle,
            contributionType);
}
