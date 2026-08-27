using XREngine.Animation.Importers;

namespace XREngine.Animation;

/// <summary>
/// Immutable state-machine leaf sample retained alongside the ordinary pose
/// value store until the target-avatar evaluator can project Unity Body motion.
/// </summary>
public readonly record struct UnityHumanoidMotionContribution(
    AnimationClip Clip,
    UnityHumanoidRootMotionPolicy Policy,
    ulong OccurrenceId,
    ulong LifecycleGeneration,
    float Weight,
    float SampleTime,
    float SamplePhase,
    long PlaybackLoopCycle,
    long SourceLoopCycle,
    EUnityHumanoidMotionContributionType ContributionType)
{
    public UnityHumanoidMotionContribution WithWeightAndType(
        float weight,
        EUnityHumanoidMotionContributionType contributionType)
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
