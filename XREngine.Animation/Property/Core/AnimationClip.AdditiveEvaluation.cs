namespace XREngine.Animation;

public partial class AnimationClip
{
    private readonly AnimationValueStore _additivePoseScratch = new();

    /// <summary>
    /// Preallocates the clip-local scratch pose used to derive additive deltas.
    /// </summary>
    internal void PrepareAdditivePoseEvaluation(AnimationSlotLayout layout)
        => _additivePoseScratch.Resize(layout);

    /// <summary>
    /// Samples this clip at one occurrence-local normalized phase. Additive clips
    /// are converted to a delta from this clip's time-zero pose before a parent
    /// blend tree sees the result, matching Unity's per-clip reference semantics.
    /// </summary>
    internal void EvaluateClipAnimationValuesAtNormalizedStateTime(
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        bool additive)
    {
        SeekClipPlaybackFromNormalizedStateTime(normalizedTime);
        GetAnimationValues(null, variables, 1.0f);
        if (!additive || SlotLayout is null)
            return;

        _additivePoseScratch.CopyFrom(ValueStore);
        SeekClipPlaybackFromNormalizedStateTime(0.0);
        GetAnimationValues(null, variables, 1.0f);
        _additivePoseScratch.MakeAdditiveRelativeTo(ValueStore);
        ValueStore.CopyFrom(_additivePoseScratch);

        // Reference sampling mutates the shared property-animation clocks. Restore
        // this occurrence's current phase without evaluating a second visible pose.
        SeekClipPlaybackFromNormalizedStateTime(normalizedTime);
    }
}
