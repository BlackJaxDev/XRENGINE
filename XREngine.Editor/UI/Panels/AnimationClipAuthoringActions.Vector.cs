using System.Numerics;
using XREngine.Animation;

namespace XREngine.Editor;

internal static partial class AnimationClipAuthoringActions
{
    private static bool EditVectorKey<T>(AnimationClip clip,
        VectorKeyframe<T> key, string description,
        Action<VectorKeyframe<T>> edit) where T : unmanaged
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
        edit(staged);
        if (!staged.TryCaptureAuthoredState(out var after) || after == before)
            return false;
        bool StillOwned() => owns!() &&
            ReferenceEquals(key.OwningTrack, track) &&
            MatchesOrder(track!, order);
        return Apply(clip, description,
            () => { if (!key.TryApplyAuthoredState(before, after, track))
                throw new InvalidOperationException("Animation vector key changed before edit."); },
            () => { if (!key.TryApplyAuthoredState(after, before, track))
                throw new InvalidOperationException("Animation vector key changed before rollback."); },
            () => key.TryCaptureAuthoredState(out var state) && state == after,
            () => key.TryCaptureAuthoredState(out var state) && state == before,
            StillOwned, additionalOwners: [clip.RootMember!, animation!, track!, key]);
    }

    private static bool EditPlanarValue(AnimationClip clip, Keyframe key,
        object value)
        => key switch
        {
            VectorKeyframe<float> scalar when value is float next =>
                EditVectorKey(clip, scalar, "Edit Animation Key Value",
                    staged => staged.OutValue = next),
            VectorKeyframe<Vector2> v2 when value is Vector2 next =>
                EditVectorKey(clip, v2, "Edit Animation Key Value",
                    staged => staged.OutValue = next),
            VectorKeyframe<Vector3> v3 when value is Vector3 next =>
                EditVectorKey(clip, v3, "Edit Animation Key Value",
                    staged => staged.OutValue = next),
            VectorKeyframe<Vector4> v4 when value is Vector4 next =>
                EditVectorKey(clip, v4, "Edit Animation Key Value",
                    staged => staged.OutValue = next),
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
