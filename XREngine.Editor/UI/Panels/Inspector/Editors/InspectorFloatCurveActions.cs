using XREngine.Animation;

namespace XREngine.Editor;

/// <summary>Edits one retained inspector curve through exact animation transactions.</summary>
internal static class InspectorFloatCurveActions
{
    public static bool Add(InspectorFloatCurveSource source, float second,
        out FloatKeyframe? added)
    {
        added = null;
        if (!source.IsCurrent() || !float.IsFinite(second) ||
            second < 0 || second > source.Curve.LengthInSeconds ||
            second > source.Track.LengthInSeconds ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } before)
            return false;
        FloatKeyframe key = new(second, source.Curve.DefaultValue, 0,
            EVectorInterpType.Linear);
        Func<bool>? payload = AnimationClipAuthoringActions.CaptureKeyPayload(key);
        if (payload is null)
            return false;
        Keyframe[] after = AnimationClipAuthoringActions.OrderWithInsertedKey(before,
            key, second);
        bool result = Apply(source, "Add Animation Key",
            () => Exact(source.Track, before, after, key, second, -1, second, -1),
            () => Exact(source.Track, after, before, key, second, -1, second, -1),
            () => ReferenceEquals(key.OwningTrack, source.Track) &&
                key.Second == second && key.AuthoredFrameIndex == -1 &&
                payload() && AnimationClipAuthoringActions.MatchesOrder(source.Track, after),
            () => key.OwningTrack is null && key.Second == second &&
                key.AuthoredFrameIndex == -1 && payload() &&
                AnimationClipAuthoringActions.MatchesOrder(source.Track, before),
            key);
        if (result) added = key;
        return result;
    }

    public static bool Remove(InspectorFloatCurveSource source, FloatKeyframe key)
    {
        if (!source.IsCurrent() || !ReferenceEquals(key.OwningTrack, source.Track) ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } before ||
            !before.Contains(key, ReferenceEqualityComparer.Instance) ||
            AnimationClipAuthoringActions.CaptureKeyPayload(key) is not { } payload)
            return false;
        float second = key.Second;
        int frame = key.AuthoredFrameIndex;
        Keyframe[] after = [.. before.Where(candidate => !ReferenceEquals(candidate, key))];
        return Apply(source, "Remove Animation Key",
            () => Exact(source.Track, before, after, key, second, frame, second, frame),
            () => Exact(source.Track, after, before, key, second, frame, second, frame),
            () => key.OwningTrack is null && key.Second == second &&
                key.AuthoredFrameIndex == frame && payload() &&
                AnimationClipAuthoringActions.MatchesOrder(source.Track, after),
            () => ReferenceEquals(key.OwningTrack, source.Track) &&
                key.Second == second && key.AuthoredFrameIndex == frame &&
                payload() && AnimationClipAuthoringActions.MatchesOrder(source.Track, before),
            key);
    }

    public static bool SetTime(InspectorFloatCurveSource source,
        FloatKeyframe key, float second)
    {
        if (!source.IsCurrent() || !float.IsFinite(second) || second < 0 ||
            second > source.Curve.LengthInSeconds ||
            second > source.Track.LengthInSeconds ||
            !ReferenceEquals(key.OwningTrack, source.Track) ||
            key.Second == second ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } before ||
            !before.Contains(key, ReferenceEqualityComparer.Instance) ||
            AnimationClipAuthoringActions.CaptureKeyPayload(key) is not { } payload)
            return false;
        float previous = key.Second;
        int frame = key.AuthoredFrameIndex;
        Keyframe[] after = AnimationClipAuthoringActions.OrderWithMovedKey(before,
            key, second);
        return Apply(source, "Move Animation Key",
            () => Exact(source.Track, before, after, key, previous, frame, second, -1),
            () => Exact(source.Track, after, before, key, second, -1, previous, frame),
            () => ReferenceEquals(key.OwningTrack, source.Track) &&
                key.Second == second && key.AuthoredFrameIndex == -1 && payload() &&
                AnimationClipAuthoringActions.MatchesOrder(source.Track, after),
            () => ReferenceEquals(key.OwningTrack, source.Track) &&
                key.Second == previous && key.AuthoredFrameIndex == frame && payload() &&
                AnimationClipAuthoringActions.MatchesOrder(source.Track, before), key);
    }

    public static bool SetValue(InspectorFloatCurveSource source,
        FloatKeyframe key, float value)
    {
        if (!float.IsFinite(value)) return false;
        return EditState(source, key, "Edit Animation Key Value",
            staged => staged.OutValue = value);
    }

    public static bool SetIncomingValue(InspectorFloatCurveSource source,
        FloatKeyframe key, float value)
    {
        if (!float.IsFinite(value)) return false;
        return EditState(source, key, "Edit Animation Incoming Value",
            staged => staged.InValue = value);
    }

    public static bool SetTangent(InspectorFloatCurveSource source,
        FloatKeyframe key, bool outgoing, float value)
    {
        if (!float.IsFinite(value)) return false;
        return EditState(source, key, outgoing ? "Edit Animation Out Tangent" :
            "Edit Animation In Tangent", staged =>
            {
                if (outgoing) staged.OutTangent = value;
                else staged.InTangent = value;
            });
    }

    public static bool SetSyncValues(InspectorFloatCurveSource source,
        FloatKeyframe key, bool enabled)
        => EditState(source, key, "Link Animation Values",
            staged => staged.SyncInOutValues = enabled);

    public static bool SetSyncDirections(InspectorFloatCurveSource source,
        FloatKeyframe key, bool enabled)
        => EditState(source, key, "Link Animation Tangent Directions",
            staged => staged.SyncInOutTangentDirections = enabled);

    public static bool SetSyncMagnitudes(InspectorFloatCurveSource source,
        FloatKeyframe key, bool enabled)
        => EditState(source, key, "Link Animation Tangent Magnitudes",
            staged => staged.SyncInOutTangentMagnitudes = enabled);

    public static bool SetInterpolation(InspectorFloatCurveSource source,
        FloatKeyframe key, bool outgoing, EVectorInterpType mode)
    {
        if (!Enum.IsDefined(mode)) return false;
        return EditState(source, key, "Set Animation Key Interpolation",
            staged =>
            {
                if (outgoing) staged.InterpolationTypeOut = mode;
                else staged.InterpolationTypeIn = mode;
            });
    }

    private static bool EditState(InspectorFloatCurveSource source,
        FloatKeyframe key, string description, Action<FloatKeyframe> edit)
    {
        if (!source.IsCurrent() ||
            !ReferenceEquals(key.OwningTrack, source.Track) ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } order ||
            !order.Contains(key, ReferenceEqualityComparer.Instance) ||
            !key.TryCaptureAuthoredState(out var before))
            return false;
        float second = key.Second;
        int frame = key.AuthoredFrameIndex;
        FloatKeyframe staged = new();
        staged.RestorePublishedHandles(before.InValue, before.OutValue,
            before.InTangent, before.OutTangent,
            before.InterpolationIn, before.InterpolationOut,
            before.SyncValues, before.SyncDirections, before.SyncMagnitudes);
        edit(staged);
        if (!staged.TryCaptureAuthoredState(out var after) || after == before ||
            !float.IsFinite(after.InValue) || !float.IsFinite(after.OutValue) ||
            !float.IsFinite(after.InTangent) || !float.IsFinite(after.OutTangent))
            return false;
        bool Owns() => source.IsCurrent() &&
            ReferenceEquals(key.OwningTrack, source.Track) &&
            key.Second == second && key.AuthoredFrameIndex == frame &&
            AnimationClipAuthoringActions.MatchesOrder(source.Track, order);
        return AnimationAuthoringTransaction.Apply(source.Curve, source.Documents,
            description,
            () => ApplyState(key, before, after, source.Track),
            () => ApplyState(key, after, before, source.Track),
            () => key.Second == second && key.AuthoredFrameIndex == frame &&
                key.TryCaptureAuthoredState(out var state) && state == after,
            () => key.Second == second && key.AuthoredFrameIndex == frame &&
                key.TryCaptureAuthoredState(out var state) && state == before,
            Owns, null, [.. source.ReservationOwners, key]);
    }

    public static bool SetLength(InspectorFloatCurveSource source, float seconds,
        bool stretch)
    {
        if (!source.IsCurrent() || !float.IsFinite(seconds) ||
            seconds < 0 || seconds > 3600 ||
            AnimationClipAuthoringActions.CaptureOrder(source.Track) is not { } order)
            return false;
        Func<bool>[] payloads = new Func<bool>[order.Length];
        for (int index = 0; index < order.Length; index++)
            if (AnimationClipAuthoringActions.CaptureKeyPayload(order[index]) is { } payload)
                payloads[index] = payload;
            else return false;
        if (!AnimationLengthEdit.TryCapture(source.Curve, source.Track, order,
                payloads, seconds, stretch, out AnimationLengthEdit? edit, out _) ||
            edit is null || !edit.HasChange)
            return false;
        bool Matches(bool after) => source.IsCurrent() && edit.Matches(after);
        void Commit(bool forward)
        {
            if (!Matches(!forward) || !edit.CanCommit(forward))
                throw new InvalidOperationException("Animation length source changed.");
            edit.Commit(forward);
            void Validate()
            {
                if (!Matches(forward) || !edit.MatchesCurrentCommit(forward))
                    throw new InvalidOperationException("Animation length changed during notification.");
            }
            Validate();
            edit.Publish(forward, Validate);
        }
        return AnimationAuthoringTransaction.Apply(source.Curve, source.Documents,
            stretch ? "Stretch Animation Length" : "Set Animation Length",
            () => Commit(true), () => Commit(false),
            () => Matches(true), () => Matches(false), source.IsCurrent,
            null, [.. source.ReservationOwners, .. order]);
    }

    private static bool Apply(InspectorFloatCurveSource source, string description,
        Action forward, Action reverse, Func<bool> applied, Func<bool> restored,
        Keyframe key)
        => AnimationAuthoringTransaction.Apply(source.Curve, source.Documents,
            description, forward, reverse, applied, restored, source.IsCurrent,
            null, [.. source.ReservationOwners, key]);

    private static void Exact(BaseKeyframeTrack track, Keyframe[] before,
        Keyframe[] after, Keyframe key, float fromSecond, int fromFrame,
        float toSecond, int toFrame)
    {
        if (!track.TryApplyExactKeyEdit(before, after, key,
                fromSecond, fromFrame, toSecond, toFrame))
            throw new InvalidOperationException("Animation key changed before exact edit.");
    }

    private static void ApplyState(FloatKeyframe key,
        VectorKeyframeAuthoredState<float> before,
        VectorKeyframeAuthoredState<float> after, BaseKeyframeTrack track)
    {
        if (!key.TryApplyAuthoredState(before, after, track))
            throw new InvalidOperationException("Animation key changed before authored edit.");
    }
}
