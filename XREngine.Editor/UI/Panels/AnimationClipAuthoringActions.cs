using System.Collections;
using System.Numerics;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Editor.UI;

namespace XREngine.Editor;

/// <summary>Applies clip key edits through the editor undo and dirty paths.</summary>
internal static partial class AnimationClipAuthoringActions
{
    private static readonly object ReservationGate = new();
    private static readonly HashSet<object> ReservedOwners =
        new(ReferenceEqualityComparer.Instance);
    public static bool SetClipLength(AnimationClip clip, float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0 || seconds > 3600)
            return false;
        float previous = clip.LengthInSeconds;
        if (MathF.Abs(previous - seconds) < 0.000001f)
            return false;
        return Apply(clip, "Set Animation Length",
            () => clip.LengthInSeconds = seconds,
            () => clip.LengthInSeconds = previous,
            () => clip.LengthInSeconds == seconds,
            () => clip.LengthInSeconds == previous,
            ownedProperties: [(clip, nameof(AnimationClip.LengthInSeconds))]);
    }

    public static bool SetClipSampleRate(AnimationClip clip, int framesPerSecond)
    {
        if (framesPerSecond is < 1 or > 960)
            return false;
        int previous = clip.SampleRate;
        if (previous == framesPerSecond)
            return false;
        return Apply(clip, "Set Animation Sample Rate",
            () => clip.SampleRate = framesPerSecond,
            () => clip.SampleRate = previous,
            () => clip.SampleRate == framesPerSecond,
            () => clip.SampleRate == previous,
            ownedProperties: [(clip, nameof(AnimationClip.SampleRate))]);
    }

    public static bool SetClipTraversalMethod(AnimationClip clip,
        EAnimTreeTraversalMethod method)
    {
        if (!Enum.IsDefined(method))
            return false;
        EAnimTreeTraversalMethod previous = clip.TraversalMethod;
        if (previous == method)
            return false;
        return Apply(clip, "Set Animation Traversal",
            () => clip.TraversalMethod = method,
            () => clip.TraversalMethod = previous,
            () => clip.TraversalMethod == method,
            () => clip.TraversalMethod == previous,
            ownedProperties: [(clip, nameof(AnimationClip.TraversalMethod))]);
    }

    public static bool SetClipKind(AnimationClip clip, EAnimationClipKind kind)
    {
        if (!Enum.IsDefined(kind))
            return false;
        EAnimationClipKind previous = clip.ClipKind;
        if (previous == kind)
            return false;
        return Apply(clip, "Set Animation Clip Kind",
            () => clip.ClipKind = kind,
            () => clip.ClipKind = previous,
            () => clip.ClipKind == kind,
            () => clip.ClipKind == previous,
            ownedProperties: [(clip, nameof(AnimationClip.ClipKind))]);
    }

    public static bool SetClipLooped(AnimationClip clip, bool looped)
    {
        bool previous = clip.Looped;
        if (previous == looped)
            return false;
        return Apply(clip, "Set Animation Loop",
            () => clip.Looped = looped,
            () => clip.Looped = previous,
            () => clip.Looped == looped,
            () => clip.Looped == previous,
            ownedProperties: [(clip, nameof(AnimationClip.Looped))]);
    }

    public static bool TryAddKey(AnimationClip clip, AnimationMember member, float second, out Keyframe? key)
    {
        key = null;
        if (member.Animation is not { } animation || !float.IsFinite(second) ||
            !TryCaptureMemberOwner(clip, member, animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns))
            return false;

        float time = Math.Clamp(second, 0.0f, MathF.Max(0.0f, clip.LengthInSeconds));
        object? value = member.DefaultValue ?? DefaultValue(animation);
        key = animation switch
        {
            PropAnimFloat a => new FloatKeyframe(time, value is float f ? f : a.DefaultValue, 0.0f, EVectorInterpType.Linear),
            PropAnimVector2 a => new Vector2Keyframe(time, value is Vector2 v ? v : a.DefaultValue, Vector2.Zero, EVectorInterpType.Smooth),
            PropAnimVector3 a => new Vector3Keyframe(time, value is Vector3 v ? v : a.DefaultValue, Vector3.Zero, EVectorInterpType.Smooth),
            PropAnimVector4 a => new Vector4Keyframe(time, value is Vector4 v ? v : a.DefaultValue, Vector4.Zero, EVectorInterpType.Smooth),
            PropAnimQuaternion a => new QuaternionKeyframe(time, value is Quaternion q ? q : a.DefaultValue, Quaternion.Identity, ERadialInterpType.Smooth),
            PropAnimBool a => new BoolKeyframe(time, value is bool b ? b : a.DefaultValue),
            _ => null
        };
        if (key is null)
            return false;

        Keyframe addedKey = key;
        Func<bool>? payload = CaptureKeyPayload(addedKey);
        int addedFrame = addedKey.AuthoredFrameIndex;
        if (payload is null)
        {
            key = null;
            return false;
        }
        if (time > track!.LengthInSeconds)
        {
            key = null;
            return false;
        }
        if (CaptureOrder(track) is not { } before)
            return false;
        Keyframe[] after = OrderWithInsertedKey(before, addedKey, time);
        bool applied = Apply(clip, "Add Animation Key",
            () => Add(animation, addedKey),
            () => { if (ReferenceEquals(addedKey.OwningTrack, track)) addedKey.Remove(); },
            () => ReferenceEquals(addedKey.OwningTrack, track) &&
                addedKey.Second == time &&
                addedKey.AuthoredFrameIndex == addedFrame && payload() &&
                MatchesOrder(track, after),
            () => addedKey.OwningTrack is null &&
                addedKey.Second == time &&
                addedKey.AuthoredFrameIndex == addedFrame && payload() &&
                MatchesOrder(track, before),
            owns, additionalOwners: [member, animation, track, addedKey]);
        if (!applied)
            key = null;
        return applied;
    }

    public static bool RemoveKey(AnimationClip clip, AnimationMember member, Keyframe key)
    {
        if (member.Animation is not { } animation ||
            !ContainsKey(animation, key) ||
            !TryCaptureMemberOwner(clip, member, animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track))
            return false;
        if (CaptureOrder(track!) is not { } before)
            return false;
        Func<bool>? payload = CaptureKeyPayload(key);
        if (payload is null)
            return false;
        float originalSecond = key.Second;
        int originalFrame = key.AuthoredFrameIndex;
        Keyframe[] after = [.. before.Where(candidate =>
            !ReferenceEquals(candidate, key))];
        return Apply(clip, "Remove Animation Key",
            () => { if (!track.TryApplyExactKeyEdit(before, after, key,
                originalSecond, originalFrame, originalSecond, originalFrame))
                throw new InvalidOperationException("Animation key changed before removal."); },
            () => { if (!track.TryApplyExactKeyEdit(after, before, key,
                originalSecond, originalFrame, originalSecond, originalFrame))
                throw new InvalidOperationException("Animation key changed before restore."); },
            () => key.OwningTrack is null && key.Second == originalSecond &&
                key.AuthoredFrameIndex == originalFrame && payload() &&
                MatchesOrder(track, after),
            () => ReferenceEquals(key.OwningTrack, track) &&
                key.Second == originalSecond &&
                key.AuthoredFrameIndex == originalFrame && payload() &&
                MatchesOrder(track, before),
            owns, additionalOwners: [member, animation, track, key]);
    }

    public static bool SetKeyTime(AnimationClip clip, Keyframe key, float second)
    {
        if (!float.IsFinite(second) ||
            !TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track))
            return false;
        float next = Math.Clamp(second, 0.0f, MathF.Max(0.0f, clip.LengthInSeconds));
        if (next > track!.LengthInSeconds)
            return false;
        float previous = key.Second;
        if (MathF.Abs(previous - next) <= 0.000001f)
            return false;
        int previousFrame = key.AuthoredFrameIndex;
        if (CaptureOrder(track) is not { } before)
            return false;
        Func<bool>? payload = CaptureKeyPayload(key);
        if (payload is null)
            return false;
        Keyframe[] after = OrderWithMovedKey(before, key, next);
        return Apply(clip, "Move Animation Key",
            () => { if (!track.TryApplyExactKeyEdit(before, after, key,
                previous, previousFrame, next, -1))
                throw new InvalidOperationException("Animation key changed before time edit."); },
            () => { if (!track.TryApplyExactKeyEdit(after, before, key,
                next, -1, previous, previousFrame))
                throw new InvalidOperationException("Animation key changed before time restore."); },
            () => ReferenceEquals(key.OwningTrack, track) &&
                key.Second == next && key.AuthoredFrameIndex == -1 &&
                payload() && MatchesOrder(track, after),
            () => ReferenceEquals(key.OwningTrack, track) &&
                key.Second == previous && key.AuthoredFrameIndex == previousFrame &&
                payload() && MatchesOrder(track, before),
            owns, [(key, nameof(Keyframe.Second)),
                (key, nameof(Keyframe.AuthoredFrameIndex))],
            clip.RootMember!, animation!, track, key);
    }

    public static bool SetKeyFrame(AnimationClip clip, Keyframe key, int frame)
    {
        if (frame < 0 || clip.SampleRate <= 0 ||
            !TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track))
            return false;
        float second = frame / (float)clip.SampleRate;
        if (!float.IsFinite(second) || second > clip.LengthInSeconds ||
            second > track!.LengthInSeconds)
            return false;
        float previousSecond = key.Second;
        int previousFrame = key.AuthoredFrameIndex;
        if (MathF.Abs(previousSecond - second) <= 0.000001f && previousFrame == frame)
            return false;
        if (CaptureOrder(track) is not { } before)
            return false;
        Func<bool>? payload = CaptureKeyPayload(key);
        if (payload is null)
            return false;
        Keyframe[] after = previousSecond == second
            ? before : OrderWithMovedKey(before, key, second);
        return Apply(clip, "Set Animation Key Frame",
            () => { if (!track.TryApplyExactKeyEdit(before, after, key,
                previousSecond, previousFrame, second, frame))
                throw new InvalidOperationException("Animation key changed before frame edit."); },
            () => { if (!track.TryApplyExactKeyEdit(after, before, key,
                second, frame, previousSecond, previousFrame))
                throw new InvalidOperationException("Animation key changed before frame restore."); },
            () => ReferenceEquals(key.OwningTrack, track) &&
                key.Second == second && key.AuthoredFrameIndex == frame &&
                payload() && MatchesOrder(track, after),
            () => ReferenceEquals(key.OwningTrack, track) &&
                key.Second == previousSecond &&
                key.AuthoredFrameIndex == previousFrame &&
                payload() && MatchesOrder(track, before),
            owns, [(key, nameof(Keyframe.Second)),
                (key, nameof(Keyframe.AuthoredFrameIndex))],
            clip.RootMember!, animation!, track, key);
    }

    public static bool SetKeyTangent(AnimationClip clip, Keyframe key,
        bool outgoing, object value)
    {
        if (value is null ||
            value.GetType() != key.ValueType)
            return false;
        if (key is IPlanarKeyframe)
            return EditPlanarTangent(clip, key, outgoing, value);
        if (key is QuaternionKeyframe quaternion && value is Quaternion tangent)
        {
            if (!TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                    out BaseKeyframeTrack? track, out Func<bool>? owns) ||
                !ReferenceEquals(key.OwningTrack, track))
                return false;
            if (CaptureOrder(track!) is not { } order)
                return false;
            Quaternion previous = outgoing ? quaternion.OutTangent : quaternion.InTangent;
            if (previous == tangent)
                return false;
            return Apply(clip, outgoing ? "Set Out Tangent" : "Set In Tangent",
                () => { if (outgoing) quaternion.OutTangent = tangent;
                    else quaternion.InTangent = tangent; },
                () => { if (outgoing) quaternion.OutTangent = previous;
                    else quaternion.InTangent = previous; },
                () => (outgoing ? quaternion.OutTangent : quaternion.InTangent) == tangent,
                () => (outgoing ? quaternion.OutTangent : quaternion.InTangent) == previous,
                () => owns!() && ReferenceEquals(key.OwningTrack, track) &&
                    MatchesOrder(track, order),
                [(quaternion, outgoing ? nameof(QuaternionKeyframe.OutTangent) :
                    nameof(QuaternionKeyframe.InTangent))],
                clip.RootMember!, animation!, track, key);
        }
        return false;
    }

    public static bool SetKeyInterpolation(AnimationClip clip, Keyframe key,
        bool outgoing, EVectorInterpType mode)
    {
        return Enum.IsDefined(mode) &&
            EditPlanarInterpolation(clip, key, outgoing, mode);
    }

    public static bool SetKeyInterpolation(AnimationClip clip,
        QuaternionKeyframe key, bool outgoing, ERadialInterpType mode)
    {
        if (!Enum.IsDefined(mode) ||
            !TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track))
            return false;
        if (CaptureOrder(track!) is not { } order)
            return false;
        ERadialInterpType previous = outgoing ? key.InterpolationTypeOut :
            key.InterpolationTypeIn;
        if (previous == mode)
            return false;
        return Apply(clip, "Set Key Interpolation",
            () => { if (outgoing) key.InterpolationTypeOut = mode;
                else key.InterpolationTypeIn = mode; },
            () => { if (outgoing) key.InterpolationTypeOut = previous;
                else key.InterpolationTypeIn = previous; },
            () => (outgoing ? key.InterpolationTypeOut :
                key.InterpolationTypeIn) == mode,
            () => (outgoing ? key.InterpolationTypeOut :
                key.InterpolationTypeIn) == previous,
            () => owns!() && ReferenceEquals(key.OwningTrack, track) &&
                MatchesOrder(track, order),
            [(key, outgoing ? nameof(QuaternionKeyframe.InterpolationTypeOut) :
                nameof(QuaternionKeyframe.InterpolationTypeIn))],
            clip.RootMember!, animation!, track, key);
    }

    internal static EVectorInterpType? GetIncomingInterpolation(Keyframe key)
        => key switch
        {
            VectorKeyframe<float> value => value.InterpolationTypeIn,
            VectorKeyframe<Vector2> value => value.InterpolationTypeIn,
            VectorKeyframe<Vector3> value => value.InterpolationTypeIn,
            VectorKeyframe<Vector4> value => value.InterpolationTypeIn,
            _ => null,
        };


    public static bool SetKeyValue(AnimationClip clip, Keyframe key, object value)
    {
        if (value is null)
            return false;
        if (key is IPlanarKeyframe && value.GetType() == key.ValueType)
            return EditPlanarValue(clip, key, value, outgoing: true);
        if (key is QuaternionKeyframe quaternion && value is Quaternion rotation)
        {
            if (!TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                    out BaseKeyframeTrack? track, out Func<bool>? owns) ||
                !ReferenceEquals(key.OwningTrack, track))
                return false;
            if (CaptureOrder(track!) is not { } order)
                return false;
            Quaternion previous = quaternion.OutValue;
            if (previous == rotation)
                return false;
            return Apply(clip, "Edit Animation Key Value",
                () => quaternion.OutValue = rotation, () => quaternion.OutValue = previous,
                () => quaternion.OutValue == rotation, () => quaternion.OutValue == previous,
                () => owns!() && ReferenceEquals(key.OwningTrack, track) &&
                    MatchesOrder(track, order),
                [(quaternion, nameof(QuaternionKeyframe.OutValue))],
                clip.RootMember!, animation!, track, key);
        }
        if (key is BoolKeyframe boolean && value is bool next)
        {
            if (!TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                    out BaseKeyframeTrack? track, out Func<bool>? owns) ||
                !ReferenceEquals(key.OwningTrack, track))
                return false;
            if (CaptureOrder(track!) is not { } order)
                return false;
            bool previous = boolean.Value;
            if (previous == next)
                return false;
            return Apply(clip, "Edit Animation Key Value",
                () => boolean.Value = next, () => boolean.Value = previous,
                () => boolean.Value == next, () => boolean.Value == previous,
                () => owns!() && ReferenceEquals(key.OwningTrack, track) &&
                    MatchesOrder(track, order),
                [(boolean, nameof(BoolKeyframe.Value))],
                clip.RootMember!, animation!, track, key);
        }
        return false;
    }

    public static bool SetKeyIncomingValue(AnimationClip clip, Keyframe key,
        object value)
    {
        if (value is null || value.GetType() != key.ValueType)
            return false;
        if (key is IPlanarKeyframe)
            return EditPlanarValue(clip, key, value, outgoing: false);
        if (key is not QuaternionKeyframe quaternion || value is not Quaternion next ||
            !TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track) ||
            CaptureOrder(track!) is not { } order)
            return false;
        Quaternion previous = quaternion.InValue;
        if (previous == next)
            return false;
        return Apply(clip, "Edit Animation Key Incoming Value",
            () => quaternion.InValue = next,
            () => quaternion.InValue = previous,
            () => quaternion.InValue == next,
            () => quaternion.InValue == previous,
            () => owns!() && ReferenceEquals(key.OwningTrack, track) &&
                MatchesOrder(track, order),
            [(quaternion, nameof(QuaternionKeyframe.InValue))],
            clip.RootMember!, animation!, track, key);
    }

    public static bool UnifyKey(AnimationClip clip, Keyframe key)
        => key is IPlanarKeyframe && UnifyPlanarKey(clip, key);

    public static bool AutoKeyTangents(AnimationClip clip, Keyframe key)
        => key is IPlanarKeyframe && AutoPlanarTangents(clip, key);

    public static bool RemoveMember(AnimationClip clip, AnimationMember member)
    {
        AnimationMember? root = clip.RootMember;
        if (root is null)
            return false;
        Func<bool> live = CaptureClipLifetime(clip);
        int beforeCount = clip.TotalAnimCount;
        if (ReferenceEquals(root, member))
        {
            return Apply(clip, "Remove Animation Path",
                () => { clip.RootMember = null;
                    if (!live() || clip.RootMember is not null ||
                        clip.TotalAnimCount != beforeCount)
                        throw new InvalidOperationException("Animation root changed during removal.");
                    clip.TotalAnimCount = 0; },
                () => { clip.RootMember = root;
                    if (!live() || !ReferenceEquals(clip.RootMember, root) ||
                        clip.TotalAnimCount != 0)
                        throw new InvalidOperationException("Animation root changed during restore.");
                    clip.TotalAnimCount = beforeCount; },
                () => clip.RootMember is null && clip.TotalAnimCount == 0,
                () => ReferenceEquals(clip.RootMember, root) &&
                    clip.TotalAnimCount == beforeCount,
                () => clip.RootMember is null ||
                    ReferenceEquals(clip.RootMember, root),
                [(clip, nameof(AnimationClip.RootMember)),
                    (clip, nameof(AnimationClip.TotalAnimCount))],
                root);
        }
        if (!TryFindParent(root, member, out AnimationMember? parent, out int index) || parent is null)
            return false;
        if (!TryCaptureNodePath(clip, parent, out Func<bool>? ownsParent))
            return false;
        EventList<AnimationMember> children = parent.Children;
        AnimationMember[] before = [.. parent.Children];
        AnimationMember[] after = [.. before.Take(index),
            .. before.Skip(index + 1)];
        int afterCount = CountAnimations(root) - CountAnimations(member);
        return Apply(clip, "Remove Animation Path",
            () => { if (!MatchesChildren(parent, before, children))
                    throw new InvalidOperationException("Animation member order changed.");
                children.RemoveAt(index);
                if (!live() || !ownsParent!() ||
                    !MatchesChildren(parent, after, children) ||
                    clip.TotalAnimCount != beforeCount)
                    throw new InvalidOperationException("Animation member changed during removal.");
                clip.TotalAnimCount = afterCount; },
            () =>
            {
                if (!MatchesChildren(parent, after, children))
                    throw new InvalidOperationException("Animation member order changed.");
                children.Insert(index, member);
                if (!live() || !ownsParent!() ||
                    !MatchesChildren(parent, before, children) ||
                    clip.TotalAnimCount != afterCount)
                    throw new InvalidOperationException("Animation member changed during restore.");
                clip.TotalAnimCount = beforeCount;
            },
            () => ReferenceEquals(clip.RootMember, root) &&
                MatchesChildren(parent, after, children) &&
                clip.TotalAnimCount == afterCount,
            () => ReferenceEquals(clip.RootMember, root) &&
                MatchesChildren(parent, before, children) &&
                clip.TotalAnimCount == beforeCount,
            ownsParent,
            [(clip, nameof(AnimationClip.TotalAnimCount))],
            root, parent, member);
    }

    internal static bool Apply(AnimationClip clip, string description, Action forward, Action reverse,
        Func<bool> isApplied, Func<bool> isRestored,
        Func<bool>? isStillOwned = null,
        (XRBase Target, string Property)[]? ownedProperties = null,
        params object[] additionalOwners)
    {
        if (clip.IsDestroyed || clip.IsDestroyQueued)
            return false;
        var document = clip.SourceAsset;
        Guid clipIdentity = clip.ID;
        Guid documentIdentity = document.ID;
        object[] owners = [clip, .. additionalOwners];
        lock (ReservationGate)
        {
            if (owners.Any(ReservedOwners.Contains))
                return false;
            foreach (object owner in owners)
                ReservedOwners.Add(owner);
        }
        bool mutated = false;
        bool accepted = false;
        try
        {
            using IDisposable interaction = Undo.BeginUserInteraction();
            Undo.ChangeScope change = Undo.BeginChange(description);
            StructuralChangeHandle? handle = null;
            bool complete = false;
            bool settled = false;
            bool Owned() => !clip.IsDestroyed && !clip.IsDestroyQueued &&
                !document.IsDestroyed && !document.IsDestroyQueued &&
                clip.ID == clipIdentity && document.ID == documentIdentity &&
                ReferenceEquals(clip.SourceAsset, document) &&
                (isStillOwned?.Invoke() ?? true);
            void Replay(Action action)
            {
                lock (ReservationGate)
                {
                    if (owners.Any(ReservedOwners.Contains))
                        throw new InvalidOperationException("Animation source is being edited.");
                    foreach (object owner in owners)
                        ReservedOwners.Add(owner);
                }
                try { action(); }
                finally
                {
                    try
                    {
                        try { document.MarkDirty(); }
                        catch (Exception exception)
                        { Debug.LogException(exception, $"Could not mark '{description}' replay dirty."); }
                    }
                    finally
                    {
                        lock (ReservationGate)
                            foreach (object owner in owners)
                                ReservedOwners.Remove(owner);
                    }
                }
            }
            void GuardedReverse()
            {
                Replay(() =>
                {
                    if (!Owned() || !isApplied())
                        throw new InvalidOperationException("Animation undo source changed ownership or state.");
                    reverse();
                    if (!Owned() || !isRestored())
                        throw new InvalidOperationException("Animation undo did not restore its exact source.");
                });
            }
            void GuardedForward()
            {
                Replay(() =>
                {
                    if (!Owned() || !isRestored())
                        throw new InvalidOperationException("Animation redo source changed ownership or state.");
                    forward();
                    if (!Owned() || !isApplied())
                        throw new InvalidOperationException("Animation redo did not reach its exact destination.");
                });
            }
            try
            {
                handle = Undo.BeginStructuralMutation(description,
                    GuardedReverse, GuardedForward);
                try
                {
                    try
                    {
                        if (!Owned() || !isRestored())
                            throw new InvalidOperationException("Animation edit source changed before mutation.");
                        mutated = true;
                        forward();
                        accepted = Owned() && isApplied();
                        if (!accepted)
                            throw new InvalidOperationException("Animation edit did not reach its exact destination.");
                        if (ownedProperties is not null)
                            foreach ((XRBase target, string property) in ownedProperties)
                                handle.RemoveOwnedProperty(target, property);
                        settled = true;
                    }
                    catch (Exception exception)
                    {
                        accepted = false;
                        bool canReverse = false;
                        try { canReverse = mutated && Owned() && isApplied(); }
                        catch (Exception checkException)
                        { Debug.LogException(checkException, $"Could not inspect '{description}' after failure."); }
                        if (canReverse)
                            try { reverse(); }
                            catch (Exception rollbackException)
                            { Debug.LogException(rollbackException, $"Could not reverse '{description}'."); }
                        bool restored = false;
                        try { restored = Owned() && isRestored(); }
                        catch (Exception checkException)
                        { Debug.LogException(checkException, $"Could not verify '{description}' rollback."); }
                        if (!mutated || restored)
                        {
                            if (mutated && ownedProperties is not null)
                                foreach ((XRBase target, string property) in ownedProperties)
                                    handle.RemoveOwnedProperty(target, property);
                            handle.RemoveRestoredStep();
                            settled = true;
                        }
                        else
                            Debug.UI($"[NativeAnimation] '{description}' has a conflicting owner or value; Undo is guarded for recovery.");
                        Debug.LogException(exception, $"Failed animation edit '{description}'.");
                    }
                }
                finally
                {
                    if (accepted)
                        Undo.RecordStructuralChange(description + " preflight",
                            () => { if (!Owned() || !isApplied())
                                throw new InvalidOperationException("Animation undo preflight rejected changed source."); },
                            () => { if (!Owned() || !isApplied())
                                throw new InvalidOperationException("Animation redo postflight rejected changed source."); });
                    else if (!settled)
                        Undo.RecordStructuralChange(description + " conflict guard",
                            () => throw new InvalidOperationException("Animation undo requires conflict recovery."),
                            () => throw new InvalidOperationException("Animation redo requires conflict recovery."));
                }
                complete = true;
            }
            finally
            {
                try { change.Dispose(); }
                catch (Exception exception)
                {
                    Debug.LogException(exception,
                        $"Could not publish '{description}' Undo history.");
                }
                finally
                {
                    try
                    {
                        if (mutated)
                            try { document.MarkDirty(); }
                            catch (Exception exception)
                            { Debug.LogException(exception, $"Could not mark '{description}' source dirty."); }
                    }
                    finally { handle?.Dispose(); }
                }
            }
            return accepted && complete;
        }
        finally
        {
            lock (ReservationGate)
            {
                foreach (object owner in owners)
                    ReservedOwners.Remove(owner);
            }
        }
    }

    internal static bool ContainsMember(AnimationClip clip, AnimationMember member)
    {
        if (clip.RootMember is not { } root)
            return false;
        Stack<AnimationMember> pending = new();
        HashSet<AnimationMember> seen = new(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            AnimationMember current = pending.Pop();
            if (!seen.Add(current))
                continue;
            if (ReferenceEquals(current, member))
                return true;
            foreach (AnimationMember child in current.Children)
                pending.Push(child);
        }
        return false;
    }

    internal static bool ContainsKey(AnimationClip clip, Keyframe key)
    {
        if (clip.RootMember is not { } root)
            return false;
        Stack<AnimationMember> pending = new();
        HashSet<AnimationMember> seen = new(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            AnimationMember member = pending.Pop();
            if (!seen.Add(member))
                continue;
            if (member.Animation is { } animation && ContainsKey(animation, key))
                return true;
            foreach (AnimationMember child in member.Children)
                pending.Push(child);
        }
        return false;
    }

    private static bool ContainsKey(BasePropAnim animation, Keyframe key)
    {
        BaseKeyframeTrack? track = TrackFor(animation);
        if (track is null || !ReferenceEquals(key.OwningTrack, track) ||
            track.Count is < 0 or > 1_000_000)
            return false;
        Keyframe? current = track.FirstKey;
        for (int index = 0; index < track.Count && current is not null; index++)
        {
            if (ReferenceEquals(current, key))
                return true;
            current = current.Next;
        }
        return false;
    }

    private static bool TryFindParent(AnimationMember parent, AnimationMember target, out AnimationMember? result, out int index)
    {
        for (int childIndex = 0; childIndex < parent.Children.Count; childIndex++)
        {
            AnimationMember child = parent.Children[childIndex];
            if (ReferenceEquals(child, target))
            {
                result = parent;
                index = childIndex;
                return true;
            }
            if (TryFindParent(child, target, out result, out index))
                return true;
        }
        result = null;
        index = -1;
        return false;
    }

    private static void UpdateClipStats(AnimationClip clip)
        => clip.TotalAnimCount = CountAnimations(clip.RootMember);

    private static int CountAnimations(AnimationMember? member)
    {
        if (member is null)
            return 0;
        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += CountAnimations(child);
        return count;
    }

    private static object? DefaultValue(BasePropAnim animation) => animation switch
    {
        PropAnimFloat a => a.DefaultValue,
        PropAnimVector2 a => a.DefaultValue,
        PropAnimVector3 a => a.DefaultValue,
        PropAnimVector4 a => a.DefaultValue,
        PropAnimQuaternion a => a.DefaultValue,
        PropAnimBool a => a.DefaultValue,
        _ => null
    };

    private static void Add(BasePropAnim animation, Keyframe key)
    {
        switch (animation, key)
        {
            case (PropAnimFloat a, FloatKeyframe k): a.Keyframes.Add(k); break;
            case (PropAnimVector2 a, Vector2Keyframe k): a.Keyframes.Add(k); break;
            case (PropAnimVector3 a, Vector3Keyframe k): a.Keyframes.Add(k); break;
            case (PropAnimVector4 a, Vector4Keyframe k): a.Keyframes.Add(k); break;
            case (PropAnimQuaternion a, QuaternionKeyframe k): a.Keyframes.Add(k); break;
            case (PropAnimBool a, BoolKeyframe k): a.Keyframes.Add(k); break;
        }
    }
}
