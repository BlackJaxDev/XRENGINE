using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// The animation state of one binding slot: its clip copies, its stores, its lease, and the original values
/// of its target. All storage is allocated when the cutscene opens. The first write after an open or a stop
/// reads the original values, which can box a value once for each member.
/// The frame order is store write, humanoid solve, root placement, then one IK solve.
/// </summary>
internal sealed class CutsceneAnimationBinding
{
    private readonly int _slot;
    private readonly SceneNode _node;
    private readonly AnimationClipTrack _track;
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly CutsceneAnimationLease _lease = new();

    private readonly AnimationClip[] _clips;
    private readonly AnimationValueStore[] _stores;
    private readonly AnimationMember[] _members;
    private readonly bool[] _isMethod;
    private readonly AnimationValueStore _original;
    private readonly AnimationValueStore _blended;
    private readonly AnimationValueStore _result;

    private readonly HumanoidComponent? _humanoid;
    private readonly HumanoidClipPoseDriver? _driver;
    private readonly bool _placesRoot;
    private readonly bool _tracksRoot;
    private readonly bool _hasMuscleMember;
    private readonly bool[] _hasLiveOriginal;
    private readonly bool[] _ownedByDriver;

    private readonly Transform[] _hierarchy;
    private readonly Vector3[] _hierarchyTranslations;
    private readonly Quaternion[] _hierarchyRotations;
    private readonly Vector3[] _hierarchyScales;

    private readonly int[] _activeEntries;
    private readonly CutsceneTime[] _activeTimes;
    private readonly AnimationValueStore?[] _activeStores;
    private readonly float[] _activeWeights;
    private int _activeCount;
    private float _totalWeight;

    private bool _captured;
    private bool _written;
    private bool _reportedGate;
    private bool _reportedRejection;
    private bool _reportedRebind;
    private bool _reportedUnreadableMuscles;

    private CutsceneAnimationBinding(
        int slot,
        SceneNode node,
        AnimationClipTrack track,
        AnimationClip[] clips,
        AnimationSlotLayout layout,
        HumanoidComponent? humanoid,
        bool placesRoot,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        _slot = slot;
        _node = node;
        _track = track;
        _clips = clips;
        _diagnostics = diagnostics;
        _humanoid = humanoid;
        _placesRoot = placesRoot;
        _driver = humanoid is null ? null : new HumanoidClipPoseDriver(humanoid, clips.Length);

        _stores = new AnimationValueStore[clips.Length];
        for (int i = 0; i < _stores.Length; i++)
            _stores[i] = layout.CreateStore();
        _original = layout.CreateStore();
        _blended = layout.CreateStore();
        _result = layout.CreateStore();

        _members = new AnimationMember[layout.SamplingApplyMembers.Count];
        _isMethod = new bool[_members.Length];
        _hasLiveOriginal = new bool[_members.Length];
        _ownedByDriver = new bool[_members.Length];
        bool hasMethodMember = false;
        bool writesRoot = false;
        for (int i = 0; i < _members.Length; i++)
        {
            _members[i] = layout.SamplingApplyMembers[i];
            _isMethod[i] = _members[i].MemberType == EAnimationMemberType.Method;
            hasMethodMember |= _isMethod[i];
            writesRoot |= ReferenceEquals(_members[i].BoundTarget, node.Transform);
            _hasMuscleMember |= TryGetMuscle(_members[i], out _);
            _ownedByDriver[i] = humanoid is not null
                && _isMethod[i]
                && _members[i].BoundTarget is HumanoidComponent or HumanoidIKSolverComponent;
        }
        _tracksRoot = placesRoot || writesRoot;

        // A method has no readable value, so the bones under the target are the original pose.
        List<Transform> hierarchy = [];
        if (humanoid is not null || hasMethodMember)
            CollectDescendants(node.Transform, hierarchy);
        _hierarchy = [.. hierarchy];
        _hierarchyTranslations = new Vector3[_hierarchy.Length];
        _hierarchyRotations = new Quaternion[_hierarchy.Length];
        _hierarchyScales = new Vector3[_hierarchy.Length];

        _activeEntries = new int[clips.Length];
        _activeTimes = new CutsceneTime[clips.Length];
        _activeStores = new AnimationValueStore?[clips.Length];
        _activeWeights = new float[clips.Length];
    }

    /// <summary>
    /// Makes the binding of a slot: one bound clip copy for each valid clip on the animation tracks of the slot.
    /// Each problem adds one diagnostic. The method returns null if the slot has no clip to play.
    /// </summary>
    public static CutsceneAnimationBinding? TryCreate(
        int slot,
        SceneNode node,
        List<CutsceneTrack> tracks,
        int[]?[] entryByTrackAndClip,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        HumanoidComponent? humanoid = node.GetComponent<HumanoidComponent>();
        AnimationClipTrack? firstTrack = null;
        List<AnimationClip> clips = [];
        List<AnimationSamplingBinding> samplingBindings = [];
        CutsceneClipBindingRoot root = new(node);
        bool anyRootMotion = false;

        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            if (tracks[trackIndex] is not AnimationClipTrack track || track.BindingSlot != slot)
                continue;

            int[] entries = new int[track.Clips.Count];
            Array.Fill(entries, -1);
            entryByTrackAndClip[trackIndex] = entries;

            if (firstTrack is null)
                firstTrack = track;
            else
                Report(diagnostics, trackIndex, -1, $"The track '{track.Name}' shares a binding with the track '{firstTrack.Name}'. Their clips blend by weight, and only the root offset and the finish behavior of '{firstTrack.Name}' apply.");

            for (int clipIndex = 0; clipIndex < track.Clips.Count; clipIndex++)
            {
                if (track.Clips[clipIndex] is not AnimationClipTrackClip entry)
                    continue;

                AnimationClip? instance = TryPrepareInstance(entry, track, trackIndex, clipIndex, humanoid, root, diagnostics);
                if (instance is null)
                    continue;

                entries[clipIndex] = clips.Count;
                clips.Add(instance);
                samplingBindings.Add(new AnimationSamplingBinding(instance));
                anyRootMotion |= instance.HasRootMotion;
            }
        }

        if (firstTrack is null || clips.Count == 0)
            return null;

        bool hasOffset = firstTrack.RootOffsetPosition != Vector3.Zero || firstTrack.RootOffsetRotation != Quaternion.Identity;
        if (hasOffset && humanoid is null)
            Report(diagnostics, tracks.IndexOf(firstTrack), -1, $"The track '{firstTrack.Name}' has a root offset, but its target has no HumanoidComponent. The offset has no effect.");

        ReportClipComponentRemaps(node, tracks.IndexOf(firstTrack), firstTrack.Name, diagnostics);

        AnimationSlotLayout layout;
        try
        {
            layout = AnimationClip.PrepareSharedSampling(samplingBindings);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Report(diagnostics, tracks.IndexOf(firstTrack), -1, $"The clips of the track '{firstTrack.Name}' cannot share one sampling layout. They are skipped. {exception.Message}");
            return null;
        }

        return new CutsceneAnimationBinding(slot, node, firstTrack, [.. clips], layout, humanoid, humanoid is not null && (anyRootMotion || hasOffset), diagnostics);
    }

    /// <summary>Clears the clips of the last evaluation.</summary>
    public void BeginCollect()
    {
        _activeCount = 0;
        _totalWeight = 0f;
    }

    /// <summary>Adds an active clip of this slot to the evaluation.</summary>
    public void Collect(int entry, in CutsceneActiveClip active)
    {
        if (active.Weight <= 0f || _activeCount >= _activeEntries.Length)
            return;

        _activeEntries[_activeCount] = entry;
        _activeTimes[_activeCount] = active.LocalTime;
        _activeWeights[_activeCount] = active.Weight;
        _activeStores[_activeCount] = _stores[entry];
        _activeCount++;
        _totalWeight += active.Weight;
    }

    /// <summary>Writes the collected clips, or gives the original values back if no clip has weight.</summary>
    /// <param name="current">The node of the slot now. The clips bound to the node of the open, so another node gets no write.</param>
    /// <param name="snapshot">The store of the original root transforms.</param>
    public void Evaluate(SceneNode? current, CutsceneStateSnapshot snapshot)
    {
        if (!ReferenceEquals(current, _node))
        {
            if (current is not null)
                ReportRebindOnce();
            _lease.Release();
            _written = false;
            return;
        }

        if (_activeCount == 0)
        {
            if (_written)
                ReturnToOriginal(snapshot);
            _lease.Release();
            return;
        }

        // The humanoid preview mode owns the pose outside the animated preview: its solver and
        // the sibling evaluators all stop at this gate, and a write here would only reject the frame.
        if (_humanoid is { IsAnimatedPosePreviewActive: false })
        {
            ReportGateOnce();
            return;
        }

        _lease.Acquire(_node);
        CaptureOriginal(snapshot);

        float divisor = _totalWeight;
        for (int i = 0; i < _activeCount; i++)
        {
            _clips[_activeEntries[i]].SampleToStore((float)_activeTimes[i].ToSeconds(), _activeStores[i]!);
            _activeWeights[i] /= divisor;
        }

        AnimationValueStore.WeightedBlend(_activeStores, _activeWeights, _activeCount, normalizeWeights: true, _blended);
        _result.CopyFrom(_original);
        float blendToClips = MathF.Min(_totalWeight, 1f);
        _result.OverrideFrom(_blended, blendToClips);

        // A failed frame can have written some members, so the exit path must restore them.
        _written = true;
        try
        {
            if (_driver is null)
                WriteResult();
            else
                WriteHumanoidFrame(_driver, blendToClips, snapshot);
        }
        catch (Exception exception)
        {
            _driver?.CancelFrame();
            ReportRejectionOnce($"A member or a solver failed. {exception.GetBaseException().Message}");
        }
    }

    /// <summary>Ends the use of the target for a stop.</summary>
    public void EndForStop(bool forceAll, CutsceneStateSnapshot snapshot)
    {
        if (_written)
        {
            if (forceAll || _track.WhenFinished == ECutsceneFinishBehavior.RestoreState)
                ReturnToOriginal(snapshot);
            else
                _driver?.ReleaseMotion();
        }
        Forget();
    }

    /// <summary>Ends the lease and forgets the original values.</summary>
    public void Forget()
    {
        _lease.Release();
        _written = false;
        _captured = false;
    }

    private void WriteHumanoidFrame(HumanoidClipPoseDriver driver, float blendToClips, CutsceneStateSnapshot snapshot)
    {
        if (!driver.TryBeginFrame())
        {
            ReportRejectionOnce("Another evaluator owns the Body and root transaction of the humanoid.");
            return;
        }

        for (int i = 0; i < _activeCount; i++)
        {
            int entry = _activeEntries[i];
            float second = (float)_activeTimes[i].ToSeconds();
            float weight = _activeWeights[i] * blendToClips;
            if (driver.TryAddClip(entry, _clips[entry], second, weight))
                continue;

            driver.CancelFrame();
            ReportRejectionOnce($"The humanoid cannot evaluate the Body and root motion of clip '{_clips[entry].Name}'.");
            return;
        }

        driver.StageMotion();
        WriteResult();

        if (!driver.TrySolvePose(out Vector3 rootPosition, out Quaternion rootRotation))
        {
            ReportRejectionOnce("The humanoid rejected the frame. The last accepted pose stays.");
            return;
        }

        if (_placesRoot)
            PlaceRoot(rootPosition, rootRotation, blendToClips, snapshot);

        driver.SolveIK();
    }

    private void WriteResult()
    {
        for (int i = 0; i < _members.Length; i++)
        {
            // A method without a live original has only a default value when no clip authors
            // it. A call with that value would set an IK goal that no active clip asked for.
            if (_isMethod[i] && !_hasLiveOriginal[i] && !_members[i].HasValueIn(_blended))
                continue;

            _members[i].ApplyFromStore(_result);
        }
    }

    /// <summary>
    /// Writes the root as start transform, then track offset, then clip motion. The result reads only the
    /// recorded start and this frame's motion, so a seek gives the same value each time.
    /// </summary>
    private void PlaceRoot(Vector3 motionPosition, Quaternion motionRotation, float blendToClips, CutsceneStateSnapshot snapshot)
    {
        if (_node.Transform is not Transform transform
            || !snapshot.TryGetTransform(_slot, out Vector3 startPosition, out Quaternion startRotation, out _))
            return;

        Vector3 offsetPosition = _track.RootOffsetPosition * blendToClips;
        Quaternion offsetRotation = Quaternion.Slerp(Quaternion.Identity, _track.RootOffsetRotation, blendToClips);

        Vector3 local = offsetPosition + Vector3.Transform(motionPosition, offsetRotation);
        transform.SetLocalTranslationRotation(
            startPosition + Vector3.Transform(local, startRotation),
            Quaternion.Normalize(startRotation * offsetRotation * motionRotation));
    }

    private void CaptureOriginal(CutsceneStateSnapshot snapshot)
    {
        if (_captured)
            return;
        _captured = true;

        if (_tracksRoot)
            snapshot.CaptureTransform(_slot);

        _driver?.CaptureOriginalMuscles();
        _original.Clear();
        for (int i = 0; i < _members.Length; i++)
        {
            _hasLiveOriginal[i] = TryWriteLiveMethodValue(_members[i]);
            if (!_hasLiveOriginal[i])
                _members[i].TryWriteTargetValueToStore(_original);
        }

        for (int i = 0; i < _hierarchy.Length; i++)
        {
            Transform transform = _hierarchy[i];
            _hierarchyTranslations[i] = transform.Translation;
            _hierarchyRotations[i] = transform.Rotation;
            _hierarchyScales[i] = transform.Scale;
        }

        if (_hasMuscleMember && _driver is { HasOriginalMuscles: false } && IsAwayFromBindPose())
            ReportUnreadableMusclesOnce();
    }

    /// <summary>Reads the value of a muscle or an IK weight method from its target. A method has no getter of its own.</summary>
    private bool TryWriteLiveMethodValue(AnimationMember member)
    {
        if (_driver is not null && TryGetMuscle(member, out EHumanoidValue muscle))
            return _driver.TryGetOriginalMuscle(muscle, out float raw) && member.TryWriteFloatToStore(_original, raw);

        if (member.BoundTarget is not HumanoidIKSolverComponent solver
            || member.MethodArguments.Length == 0
            || member.MethodArguments[0] is not ELimbEndEffector goal)
            return false;

        return member.MemberName switch
        {
            "SetIKPositionWeight" => member.TryWriteFloatToStore(_original, solver.GetIKPositionWeight(goal)),
            "SetIKRotationWeight" => member.TryWriteFloatToStore(_original, solver.GetIKRotationWeight(goal)),
            _ => false,
        };
    }

    private static bool TryGetMuscle(AnimationMember member, out EHumanoidValue muscle)
    {
        muscle = default;
        if (member.BoundTarget is not HumanoidComponent
            || member.MemberName is not ("SetValue" or "SetImportedRawValue")
            || member.MethodArguments.Length == 0)
            return false;

        switch (member.MethodArguments[0])
        {
            case EHumanoidValue value:
                muscle = value;
                return true;
            case int index:
                muscle = (EHumanoidValue)index;
                return true;
            default:
                return false;
        }
    }

    private bool IsAwayFromBindPose()
    {
        for (int i = 0; i < _hierarchy.Length; i++)
        {
            Quaternion bind = _hierarchy[i].BindState.Rotation;
            if (MathF.Abs(Quaternion.Dot(Quaternion.Normalize(_hierarchyRotations[i]), Quaternion.Normalize(bind))) < 0.9999f)
                return true;
        }
        return false;
    }

    private void ReturnToOriginal(CutsceneStateSnapshot snapshot)
    {
        _written = false;
        _driver?.ClearPoseInputs();

        for (int i = 0; i < _members.Length; i++)
        {
            // The driver owns the muscle, Body, root, and IK goal methods: it gave the muscles back and
            // cleared the goals. A call with a default argument there would stage a new humanoid input.
            if (_ownedByDriver[i])
                continue;

            // One failed member must not stop the restore of the others, the lease release, or the stop.
            try
            {
                _members[i].ApplyFromStore(_original);
            }
            catch (Exception exception)
            {
                ReportRejectionOnce($"A member failed in the restore. {exception.GetBaseException().Message}");
            }
        }

        for (int i = 0; i < _hierarchy.Length; i++)
        {
            Transform transform = _hierarchy[i];
            transform.Translation = _hierarchyTranslations[i];
            transform.Rotation = _hierarchyRotations[i];
            transform.Scale = _hierarchyScales[i];
        }

        if (_tracksRoot)
            snapshot.ApplyTransform(_slot);
    }

    private void ReportGateOnce()
    {
        if (_reportedGate)
            return;
        _reportedGate = true;
        ReportForTrack($"The humanoid of the track '{_track.Name}' is not in the animated pose preview mode. The preview mode owns the pose, so the clips write nothing.");
    }

    private void ReportUnreadableMusclesOnce()
    {
        if (_reportedUnreadableMuscles)
            return;
        _reportedUnreadableMuscles = true;
        ReportForTrack($"The target of the track '{_track.Name}' has bones away from the bind pose but no readable muscle values. A fade of its muscles starts from the neutral pose.");
    }

    private void ReportRebindOnce()
    {
        if (_reportedRebind)
            return;
        _reportedRebind = true;
        ReportForTrack($"The binding of the track '{_track.Name}' changed to another node after the open. Clip copies are made only at open, so the clips write nothing.");
    }

    private void ReportRejectionOnce(string reason)
    {
        if (_reportedRejection)
            return;
        _reportedRejection = true;
        ReportForTrack($"The track '{_track.Name}' wrote no pose. {reason}");
    }

    private void ReportForTrack(string message)
        => Report(_diagnostics, -1, -1, message);

    private static AnimationClip? TryPrepareInstance(
        AnimationClipTrackClip entry,
        AnimationClipTrack track,
        int trackIndex,
        int clipIndex,
        HumanoidComponent? humanoid,
        CutsceneClipBindingRoot root,
        List<CutsceneCompileDiagnostic> diagnostics)
    {
        if (entry.Clip is not { } source)
        {
            Report(diagnostics, trackIndex, clipIndex, $"The clip {clipIndex} on track '{track.Name}' has no animation clip. It is skipped.");
            return null;
        }

        if (source.HasMuscleChannels || source.HasRootMotion || source.HasIKGoals)
        {
            if (humanoid is null)
            {
                Report(diagnostics, trackIndex, clipIndex, $"The clip '{source.Name}' on track '{track.Name}' has humanoid data, but its target has no HumanoidComponent. It is skipped.");
                return null;
            }

            if (!humanoid.TryValidateAvatarDefinitionForPlayback(out string avatarProblem))
            {
                Report(diagnostics, trackIndex, clipIndex, $"The clip '{source.Name}' on track '{track.Name}' cannot use the avatar of its target. It is skipped. {avatarProblem}");
                return null;
            }
        }

        // The sampled value of an additive clip is absolute: the reference pose is not subtracted.
        if (source.ImportedHumanoidRootMotionSettings?.HasAdditiveReferencePose == true)
            Report(diagnostics, trackIndex, clipIndex, $"The clip '{source.Name}' on track '{track.Name}' is additive. A cutscene plays it as an absolute pose, without its reference pose.");

        AnimationClip instance;
        try
        {
            instance = CutsceneClipInstancer.CreateInstance(source);
        }
        catch (Exception exception)
        {
            Report(diagnostics, trackIndex, clipIndex, $"The clip '{source.Name}' on track '{track.Name}' cannot be copied. It is skipped. {exception.Message}");
            return null;
        }

        if (instance.RootMember is { } rootMember)
            BindMember(rootMember, root);

        ReportUnboundMembers(instance.RootMember, source.Name, track.Name, trackIndex, clipIndex, diagnostics);

        IReadOnlyList<AnimationMember> unsupported = instance.GetUnsupportedSamplingMembers();
        for (int i = 0; i < unsupported.Count; i++)
            Report(diagnostics, trackIndex, clipIndex, $"The member '{unsupported[i].MemberName}' of clip '{source.Name}' calls code at the sample time. A cutscene does not sample it.");

        return instance;
    }

    private static void BindMember(AnimationMember member, object? parent)
    {
        object? current = parent;
        if (member.MemberType != EAnimationMemberType.Group)
            current = member.Initialize is { } initialize ? initialize(parent) : parent;

        for (int i = 0; i < member.Children.Count; i++)
            BindMember(member.Children[i], current);
    }

    private static void ReportUnboundMembers(AnimationMember? member, string? clipName, string trackName, int trackIndex, int clipIndex, List<CutsceneCompileDiagnostic> diagnostics)
    {
        if (member is null)
            return;

        if (member.MemberNotFound)
            Report(diagnostics, trackIndex, clipIndex, $"The member '{member.MemberName}' of clip '{clipName}' on track '{trackName}' does not exist on its target. It writes nothing.");

        for (int i = 0; i < member.Children.Count; i++)
            ReportUnboundMembers(member.Children[i], clipName, trackName, trackIndex, clipIndex, diagnostics);
    }

    private static void ReportClipComponentRemaps(SceneNode node, int trackIndex, string trackName, List<CutsceneCompileDiagnostic> diagnostics)
    {
        foreach (AnimationClipComponent player in node.GetComponents<AnimationClipComponent>())
        {
            if (player.FlipMuscleLeftRight || player.FlipMuscleZ
                || player.FlipIKPositionLeftRight || player.FlipIKPositionZ
                || player.FlipIKRotationLeftRight || player.FlipIKRotationZ)
                Report(diagnostics, trackIndex, -1, $"The target of track '{trackName}' has an AnimationClipComponent with flip settings. Those runtime remaps belong to that component, and the cutscene clips do not use them.");
        }
    }

    private static void CollectDescendants(TransformBase parent, List<Transform> result)
    {
        foreach (TransformBase child in parent.Children)
        {
            if (child is Transform transform)
                result.Add(transform);
            CollectDescendants(child, result);
        }
    }

    private static void Report(List<CutsceneCompileDiagnostic> diagnostics, int trackIndex, int clipIndex, string message)
    {
        diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", message);
    }
}
