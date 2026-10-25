using System.Numerics;
using XREngine.Animation;
using XREngine.Data.Animation;

namespace XREngine.Editor;

internal static partial class AnimationClipAuthoringActions
{
    private static bool EditVectorKey<T>(AnimationClip clip,
        VectorKeyframe<T> key, string description,
        Action<VectorKeyframe<T>> edit,
        Func<bool>? dependencyIsCurrent = null,
        object[]? dependentOwners = null) where T : unmanaged
    {
        VectorKeyframe<T>? staged =
            key.GetType() == typeof(FloatKeyframe) && typeof(T) == typeof(float)
                ? (VectorKeyframe<T>)(object)new FloatKeyframe()
            : key.GetType() == typeof(Vector2Keyframe) && typeof(T) == typeof(Vector2)
                ? (VectorKeyframe<T>)(object)new Vector2Keyframe()
            : key.GetType() == typeof(Vector3Keyframe) && typeof(T) == typeof(Vector3)
                ? (VectorKeyframe<T>)(object)new Vector3Keyframe()
            : key.GetType() == typeof(Vector4Keyframe) && typeof(T) == typeof(Vector4)
                ? (VectorKeyframe<T>)(object)new Vector4Keyframe()
            : null;
        if (staged is null ||
            !TryCaptureKeyOwner(clip, key, out BasePropAnim? animation,
                out BaseKeyframeTrack? track, out Func<bool>? owns) ||
            !ReferenceEquals(key.OwningTrack, track) ||
            !key.TryCaptureAuthoredState(out var before))
            return false;
        if (CaptureOrder(track!) is not { } order)
            return false;
        staged.RestorePublishedHandles(before.InValue, before.OutValue,
            before.InTangent, before.OutTangent,
            before.InterpolationIn, before.InterpolationOut,
            before.SyncValues, before.SyncDirections,
            before.SyncMagnitudes);
        if (!staged.TryRestoreAuthoredWeights(before.WeightedMode,
                before.InWeight, before.OutWeight))
            return false;
        edit(staged);
        if (!staged.TryCaptureAuthoredState(out var after) || after == before)
            return false;
        bool StillOwned() => owns!() &&
            ReferenceEquals(key.OwningTrack, track) &&
            MatchesOrder(track!, order) &&
            (dependencyIsCurrent?.Invoke() ?? true);
        return Apply(clip, description,
            () => { if (!key.TryApplyAuthoredState(before, after, track))
                throw new InvalidOperationException("Animation vector key changed before edit."); },
            () => { if (!key.TryApplyAuthoredState(after, before, track))
                throw new InvalidOperationException("Animation vector key changed before rollback."); },
            () => key.TryCaptureAuthoredState(out var state) && state == after,
            () => key.TryCaptureAuthoredState(out var state) && state == before,
            StillOwned, additionalOwners: [clip.RootMember!, animation!, track!, key,
                .. (dependentOwners ?? [])]);
    }

    private static bool EditPlanarValue(AnimationClip clip, Keyframe key,
        object value, bool outgoing)
        => key switch
        {
            VectorKeyframe<float> scalar when value is float next =>
                EditVectorKey(clip, scalar, "Edit Animation Key Value",
                    staged => { if (outgoing) staged.OutValue = next;
                        else staged.InValue = next; }),
            VectorKeyframe<Vector2> v2 when value is Vector2 next =>
                EditVectorKey(clip, v2, "Edit Animation Key Value",
                    staged => { if (outgoing) staged.OutValue = next;
                        else staged.InValue = next; }),
            VectorKeyframe<Vector3> v3 when value is Vector3 next =>
                EditVectorKey(clip, v3, "Edit Animation Key Value",
                    staged => { if (outgoing) staged.OutValue = next;
                        else staged.InValue = next; }),
            VectorKeyframe<Vector4> v4 when value is Vector4 next =>
                EditVectorKey(clip, v4, "Edit Animation Key Value",
                    staged => { if (outgoing) staged.OutValue = next;
                        else staged.InValue = next; }),
            _ => false,
        };

    private static bool UnifyPlanarKey(AnimationClip clip, Keyframe key)
        => key switch
        {
            VectorKeyframe<float> scalar =>
                EditVectorKey(clip, scalar, "Unify Animation Key",
                    staged => staged.UnifyKeyframe(EUnifyBias.Average)),
            VectorKeyframe<Vector2> v2 =>
                EditVectorKey(clip, v2, "Unify Animation Key",
                    staged => staged.UnifyKeyframe(EUnifyBias.Average)),
            VectorKeyframe<Vector3> v3 =>
                EditVectorKey(clip, v3, "Unify Animation Key",
                    staged => staged.UnifyKeyframe(EUnifyBias.Average)),
            VectorKeyframe<Vector4> v4 =>
                EditVectorKey(clip, v4, "Unify Animation Key",
                    staged => staged.UnifyKeyframe(EUnifyBias.Average)),
            _ => false,
        };

    private static bool AutoVectorKey<T>(AnimationClip clip,
        VectorKeyframe<T> key, Func<T, T, float, T> incomingSlope,
        Func<T, T, float, T> outgoingSlope) where T : unmanaged
    {
        if (key.OwningTrack is not { } track)
            return false;
        float second = key.Second;
        float length = track.LengthInSeconds;
        EKeyframeInfinityMode beforeMode = track.PreInfinityMode;
        EKeyframeInfinityMode afterMode = track.PostInfinityMode;
        VectorKeyframe<T>? originalPrev = key.Prev;
        VectorKeyframe<T>? originalNext = key.Next;
        VectorKeyframe<T>? previous = originalPrev ??
            (track.LoopsBeforeFirstKey && !ReferenceEquals(track.LastKey, key)
                ? track.LastKey as VectorKeyframe<T> : null);
        VectorKeyframe<T>? next = originalNext ??
            (track.LoopsAfterLastKey && !ReferenceEquals(track.FirstKey, key)
                ? track.FirstKey as VectorKeyframe<T> : null);
        VectorKeyframeAuthoredState<T> previousState = default;
        VectorKeyframeAuthoredState<T> nextState = default;
        if (previous is not null &&
            !previous.TryCaptureAuthoredState(out previousState))
            return false;
        if (next is not null && !next.TryCaptureAuthoredState(out nextState))
            return false;
        float previousSecond = previous?.Second ?? 0;
        float nextSecond = next?.Second ?? 0;
        float incomingSpan = previous is null ? 0 :
            ReferenceEquals(originalPrev, previous)
                ? second - previousSecond
                : length - previousSecond + second;
        float outgoingSpan = next is null ? 0 :
            ReferenceEquals(originalNext, next)
                ? nextSecond - second
                : length - second + nextSecond;
        bool hasIncoming = previous is not null &&
            float.IsFinite(incomingSpan) && incomingSpan > 0.000001f;
        bool hasOutgoing = next is not null &&
            float.IsFinite(outgoingSpan) && outgoingSpan > 0.000001f;
        if (!hasIncoming && !hasOutgoing)
            return false;
        T incoming = hasIncoming
            ? incomingSlope(key.InValue, previousState.OutValue, incomingSpan)
            : default;
        T outgoing = hasOutgoing
            ? outgoingSlope(nextState.InValue, key.OutValue, outgoingSpan)
            : default;
        bool DependenciesCurrent() => key.Second == second &&
            track.LengthInSeconds == length &&
            track.PreInfinityMode == beforeMode &&
            track.PostInfinityMode == afterMode &&
            ReferenceEquals(key.Prev, originalPrev) &&
            ReferenceEquals(key.Next, originalNext) &&
            (previous is null ||
                previous.Second == previousSecond &&
                previous.TryCaptureAuthoredState(out var currentPrevious) &&
                currentPrevious == previousState) &&
            (next is null || next.Second == nextSecond &&
                next.TryCaptureAuthoredState(out var currentNext) &&
                currentNext == nextState);
        object[] dependentOwners = previous is null
            ? next is null ? [] : [next]
            : next is null ? [previous] : [previous, next];
        return EditVectorKey(clip, key, "Auto Animation Tangents",
            staged =>
            {
                if (hasIncoming)
                    staged.InTangent = incoming;
                if (hasOutgoing)
                    staged.OutTangent = outgoing;
            }, DependenciesCurrent, dependentOwners);
    }

    private static bool AutoPlanarTangents(AnimationClip clip, Keyframe key)
        => key switch
        {
            VectorKeyframe<float> scalar => AutoVectorKey(clip, scalar,
                (value, previous, span) => (previous - value) / span,
                (next, value, span) => (next - value) / span),
            VectorKeyframe<Vector2> v2 => AutoVectorKey(clip, v2,
                (value, previous, span) => (value - previous) / span,
                (next, value, span) => (next - value) / span),
            VectorKeyframe<Vector3> v3 => AutoVectorKey(clip, v3,
                (value, previous, span) => (value - previous) / span,
                (next, value, span) => (next - value) / span),
            VectorKeyframe<Vector4> v4 => AutoVectorKey(clip, v4,
                (value, previous, span) => (value - previous) / span,
                (next, value, span) => (next - value) / span),
            _ => false,
        };

    private static bool EditPlanarTangent(AnimationClip clip, Keyframe key,
        bool outgoing, object value)
        => key switch
        {
            VectorKeyframe<float> scalar when value is float next =>
                EditVectorKey(clip, scalar, "Set Animation Tangent",
                    staged => { if (outgoing) staged.OutTangent = next;
                        else staged.InTangent = next; }),
            VectorKeyframe<Vector2> v2 when value is Vector2 next =>
                EditVectorKey(clip, v2, "Set Animation Tangent",
                    staged => { if (outgoing) staged.OutTangent = next;
                        else staged.InTangent = next; }),
            VectorKeyframe<Vector3> v3 when value is Vector3 next =>
                EditVectorKey(clip, v3, "Set Animation Tangent",
                    staged => { if (outgoing) staged.OutTangent = next;
                        else staged.InTangent = next; }),
            VectorKeyframe<Vector4> v4 when value is Vector4 next =>
                EditVectorKey(clip, v4, "Set Animation Tangent",
                    staged => { if (outgoing) staged.OutTangent = next;
                        else staged.InTangent = next; }),
            _ => false,
        };

    private static bool EditPlanarInterpolation(AnimationClip clip,
        Keyframe key, bool outgoing, EVectorInterpType mode)
        => key switch
        {
            VectorKeyframe<float> scalar =>
                EditVectorKey(clip, scalar, "Set Animation Interpolation",
                    staged => { if (outgoing) staged.InterpolationTypeOut = mode;
                        else staged.InterpolationTypeIn = mode; }),
            VectorKeyframe<Vector2> v2 =>
                EditVectorKey(clip, v2, "Set Animation Interpolation",
                    staged => { if (outgoing) staged.InterpolationTypeOut = mode;
                        else staged.InterpolationTypeIn = mode; }),
            VectorKeyframe<Vector3> v3 =>
                EditVectorKey(clip, v3, "Set Animation Interpolation",
                    staged => { if (outgoing) staged.InterpolationTypeOut = mode;
                        else staged.InterpolationTypeIn = mode; }),
            VectorKeyframe<Vector4> v4 =>
                EditVectorKey(clip, v4, "Set Animation Interpolation",
                    staged => { if (outgoing) staged.InterpolationTypeOut = mode;
                        else staged.InterpolationTypeIn = mode; }),
            _ => false,
        };
}
