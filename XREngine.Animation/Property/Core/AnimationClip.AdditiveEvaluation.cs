namespace XREngine.Animation;

public partial class AnimationClip
{
    private readonly AnimationValueStore _additivePoseScratch = new();
    private readonly AnimationValueStore _additiveReferencePose = new();
    private readonly AnimationValueStore _additiveReferenceRestoreScratch = new();
    private static readonly Dictionary<string, AnimVar> EmptyAdditiveReferenceVariables = [];
    private BasePropAnim[] _additiveReferenceAnimations = [];
    private float[] _additiveReferenceAnimationTimes = [];
    private bool _additiveReferencePosePrepared;

    /// <summary>
    /// Preallocates the clip-local scratch pose used to derive additive deltas.
    /// </summary>
    internal void PrepareAdditivePoseEvaluation(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
    {
        _additiveReferencePosePrepared = false;
        _additivePoseScratch.Resize(layout);
        _additiveReferencePose.Resize(layout);
        _additiveReferenceRestoreScratch.Resize(layout);
        if (!TryGetImportedAdditiveReferencePose(
                out AnimationClip referenceClip,
                out float referenceTimeSeconds))
            return;

        // A reference clip is not necessarily another graph leaf, so normal motion initialization
        // may never have populated its clip-local binding table. Build that table structurally here;
        // reference sampling does not need to resolve or mutate the live target object.
        referenceClip.EnsureStandaloneSamplingBindings();
        referenceClip.SlotLayout = layout;
        referenceClip.ValueStore.Resize(layout);
        foreach ((string path, AnimationMember member) in referenceClip.AnimatedCurves)
        {
            member.Slot = AnimSlot.Invalid;
            if (!slotsByPath.TryGetValue(path, out AnimSlot slot))
                continue;
            EAnimValueType valueType = member.DetermineValueType();
            if (slot.Type != valueType)
                throw new InvalidOperationException(
                    $"Unity additive reference binding '{path}' has value type {valueType}, " +
                    $"but the source clip slot uses {slot.Type}.");
            member.Slot = slot;
        }
        referenceClip.AnimatedMembersArray = [.. referenceClip.AnimatedCurves.Values.Distinct()];
        referenceClip.PrepareImportedHumanoidMirrorSlotBindings(layout, slotsByPath);
        referenceClip.PrepareImportedHumanoidScalarQuaternionBindings(layout);

        PrepareAdditiveReferenceAnimationSnapshot(referenceClip);
        ImportedHumanoidPlaybackSnapshot clockSnapshot = referenceClip.CaptureImportedHumanoidPlaybackSnapshot();
        _additiveReferenceRestoreScratch.CopyFrom(referenceClip.ValueStore);
        for (int i = 0; i < _additiveReferenceAnimations.Length; i++)
            _additiveReferenceAnimationTimes[i] = _additiveReferenceAnimations[i].CurrentTime;

        try
        {
            referenceClip.SeekClipPlaybackFromSourceSeconds(referenceTimeSeconds);
            referenceClip.GetAnimationValues(null, EmptyAdditiveReferenceVariables, 1.0f);
            _additiveReferencePose.CopyFrom(referenceClip.ValueStore);
            _additiveReferencePosePrepared = true;
        }
        finally
        {
            for (int i = 0; i < _additiveReferenceAnimations.Length; i++)
                _additiveReferenceAnimations[i].Seek(
                    _additiveReferenceAnimationTimes[i],
                    wrapLooped: false);
            referenceClip.RestoreImportedHumanoidPlaybackSnapshot(clockSnapshot);
            referenceClip.ValueStore.CopyFrom(_additiveReferenceRestoreScratch);
        }
    }

    private void EnsureStandaloneSamplingBindings()
    {
        if (AnimatedCurves.Count > 0 || RootMember is null)
            return;

        RegisterStandaloneSamplingBindings(RootMember, string.Empty);
    }

    private void RegisterStandaloneSamplingBindings(AnimationMember member, string path)
    {
        if (member.MemberType != EAnimationMemberType.Group)
            path += member.MemberName;

        if (member.MemberType == EAnimationMemberType.Method)
        {
            for (int i = 0; i < member.MethodArguments.Length; i++)
            {
                path += ":";
                object? argument = member.MethodArguments[i];
                if (member.AnimatedMethodArgumentIndex == i)
                    path += "<AnimatedValue>";
                else
                    path += argument?.ToString() ?? "<null>";
            }
        }

        if (member.Animation is not null || member.MemberType == EAnimationMemberType.Method)
            AnimatedCurves.TryAdd(path, member);

        if (member.Children.Count == 0)
            return;

        if (member.MemberType != EAnimationMemberType.Group)
            path += "/";

        foreach (AnimationMember child in member.Children)
            RegisterStandaloneSamplingBindings(child, path);
    }

    private void PrepareAdditiveReferenceAnimationSnapshot(AnimationClip referenceClip)
    {
        HashSet<BasePropAnim> animations = [];
        foreach (AnimationMember member in referenceClip.AnimatedCurves.Values)
            if (member.Animation is BasePropAnim animation)
                animations.Add(animation);
        _additiveReferenceAnimations = [.. animations];
        _additiveReferenceAnimationTimes = new float[_additiveReferenceAnimations.Length];
    }

    /// <summary>
    /// Samples this clip at one occurrence-local normalized phase. Additive clips
    /// are converted to a delta from the resolved Unity additive reference pose
    /// before a parent blend tree sees the result.
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
        if (!_additiveReferencePosePrepared)
            throw new InvalidOperationException(
                $"Unity additive reference pose for clip '{Name}' was not prepared during state-machine initialization.");

        _additivePoseScratch.MakeAdditiveRelativeTo(_additiveReferencePose);
        ValueStore.CopyFrom(_additivePoseScratch);
    }
}
