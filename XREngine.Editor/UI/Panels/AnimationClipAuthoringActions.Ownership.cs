using XREngine.Animation;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Editor;

internal static partial class AnimationClipAuthoringActions
{
    private static Func<bool> CaptureClipLifetime(AnimationClip clip)
    {
        var document = clip.SourceAsset;
        Guid clipId = clip.ID;
        Guid documentId = document.ID;
        return () => !clip.IsDestroyed && !clip.IsDestroyQueued &&
            !document.IsDestroyed && !document.IsDestroyQueued &&
            clip.ID == clipId && document.ID == documentId &&
            ReferenceEquals(clip.SourceAsset, document);
    }

    private static BaseKeyframeTrack? TrackFor(BasePropAnim animation) => animation switch
    {
        PropAnimFloat value => value.Keyframes,
        PropAnimVector2 value => value.Keyframes,
        PropAnimVector3 value => value.Keyframes,
        PropAnimVector4 value => value.Keyframes,
        PropAnimQuaternion value => value.Keyframes,
        PropAnimBool value => value.Keyframes,
        _ => null,
    };

    private static bool TryCaptureMemberOwner(AnimationClip clip,
        AnimationMember member, BasePropAnim animation,
        [NotNullWhen(true)] out BaseKeyframeTrack? track,
        [NotNullWhen(true)] out Func<bool>? owns)
    {
        track = TrackFor(animation);
        owns = null;
        if (track is null || !ReferenceEquals(member.Animation, animation) ||
            clip.RootMember is not { } root)
            return false;
        Dictionary<AnimationMember, (AnimationMember? Parent, int Index)> parents =
            new(ReferenceEqualityComparer.Instance);
        Stack<AnimationMember> pending = new();
        parents.Add(root, (null, -1));
        pending.Push(root);
        while (pending.Count > 0 && !parents.ContainsKey(member))
        {
            AnimationMember current = pending.Pop();
            for (int index = current.Children.Count - 1; index >= 0; index--)
            {
                AnimationMember child = current.Children[index];
                if (parents.TryAdd(child, (current, index)))
                    pending.Push(child);
            }
        }
        if (!parents.ContainsKey(member))
            return false;
        List<(AnimationMember Node, AnimationMember? Parent, int Index,
            object? Container)> path = [];
        for (AnimationMember? current = member; current is not null;)
        {
            (AnimationMember? parent, int index) = parents[current];
            path.Add((current, parent, index, parent?.Children));
            current = parent;
        }
        path.Reverse();
        BaseKeyframeTrack capturedTrack = track;
        owns = () =>
        {
            if (!ReferenceEquals(clip.RootMember, root) ||
                !ReferenceEquals(member.Animation, animation) ||
                !ReferenceEquals(TrackFor(animation), capturedTrack))
                return false;
            foreach ((AnimationMember node, AnimationMember? parent, int index,
                object? container) in path)
                if (parent is not null &&
                    (!ReferenceEquals(parent.Children, container) ||
                     (uint)index >= (uint)parent.Children.Count ||
                     !ReferenceEquals(parent.Children[index], node)))
                    return false;
            return true;
        };
        return true;
    }

    private static bool TryCaptureKeyOwner(AnimationClip clip, Keyframe key,
        [NotNullWhen(true)] out BasePropAnim? animation,
        [NotNullWhen(true)] out BaseKeyframeTrack? track,
        [NotNullWhen(true)] out Func<bool>? owns)
    {
        animation = null;
        track = null;
        owns = null;
        if (clip.RootMember is not { } root)
            return false;
        HashSet<AnimationMember> seen = new(ReferenceEqualityComparer.Instance);
        Stack<AnimationMember> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            AnimationMember member = pending.Pop();
            if (!seen.Add(member))
                continue;
            if (member.Animation is { } candidate &&
                ContainsKey(candidate, key) &&
                TryCaptureMemberOwner(clip, member, candidate,
                    out track, out owns))
            {
                animation = candidate;
                return true;
            }
            foreach (AnimationMember child in member.Children)
                pending.Push(child);
        }
        return false;
    }

    private static Keyframe[]? CaptureOrder(BaseKeyframeTrack track)
    {
        if (track.Count is < 0 or > 1_000_000)
            return null;
        Keyframe[] order = new Keyframe[track.Count];
        Keyframe? previous = null;
        Keyframe? current = track.FirstKey;
        HashSet<Keyframe> seen = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < order.Length; index++)
        {
            if (current is null || !seen.Add(current) ||
                !ReferenceEquals(current.OwningTrack, track) ||
                !ReferenceEquals(current.Prev, previous))
                return null;
            order[index] = current;
            previous = current;
            current = current.Next;
        }
        return current is null && ReferenceEquals(track.LastKey, previous)
            ? order : null;
    }

    private static bool MatchesOrder(BaseKeyframeTrack track,
        IReadOnlyList<Keyframe> expected)
    {
        if (track.Count != expected.Count ||
            !ReferenceEquals(track.FirstKey,
                expected.Count == 0 ? null : expected[0]) ||
            !ReferenceEquals(track.LastKey,
                expected.Count == 0 ? null : expected[^1]))
            return false;
        Keyframe? previous = null;
        Keyframe? current = track.FirstKey;
        for (int index = 0; index < expected.Count; index++)
        {
            if (current is null || !ReferenceEquals(current, expected[index]) ||
                !ReferenceEquals(current.OwningTrack, track) ||
                !ReferenceEquals(current.Prev, previous) ||
                !ReferenceEquals(current.Next,
                    index + 1 < expected.Count ? expected[index + 1] : null))
                return false;
            previous = current;
            current = current.Next;
        }
        return current is null;
    }

    private static Func<bool>? CaptureKeyPayload(Keyframe key)
    {
        switch (key)
        {
            case FloatKeyframe scalar when key.GetType() == typeof(FloatKeyframe) &&
                scalar.TryCaptureAuthoredState(out var state):
                return () => scalar.TryCaptureAuthoredState(out var current) &&
                    current == state;
            case Vector2Keyframe vector2 when key.GetType() == typeof(Vector2Keyframe) &&
                vector2.TryCaptureAuthoredState(out var state2):
                return () => vector2.TryCaptureAuthoredState(out var current) &&
                    current == state2;
            case Vector3Keyframe vector3 when key.GetType() == typeof(Vector3Keyframe) &&
                vector3.TryCaptureAuthoredState(out var state3):
                return () => vector3.TryCaptureAuthoredState(out var current) &&
                    current == state3;
            case Vector4Keyframe vector4 when key.GetType() == typeof(Vector4Keyframe) &&
                vector4.TryCaptureAuthoredState(out var state4):
                return () => vector4.TryCaptureAuthoredState(out var current) &&
                    current == state4;
            case QuaternionKeyframe quaternion when key.GetType() == typeof(QuaternionKeyframe):
                Quaternion inValue = quaternion.InValue;
                Quaternion outValue = quaternion.OutValue;
                Quaternion inTangent = quaternion.InTangent;
                Quaternion outTangent = quaternion.OutTangent;
                ERadialInterpType interpolationIn = quaternion.InterpolationTypeIn;
                ERadialInterpType interpolationOut = quaternion.InterpolationTypeOut;
                return () => quaternion.InValue == inValue &&
                    quaternion.OutValue == outValue &&
                    quaternion.InTangent == inTangent &&
                    quaternion.OutTangent == outTangent &&
                    quaternion.InterpolationTypeIn == interpolationIn &&
                    quaternion.InterpolationTypeOut == interpolationOut;
            case BoolKeyframe boolean when key.GetType() == typeof(BoolKeyframe):
                bool value = boolean.Value;
                return () => boolean.Value == value;
            default:
                return null;
        }
    }

    private static Keyframe[] OrderWithInsertedKey(
        IReadOnlyList<Keyframe> before, Keyframe key, float second)
    {
        List<Keyframe> result = new(before.Count + 1);
        int index = 0;
        while (index < before.Count && before[index].Second <= second)
            result.Add(before[index++]);
        result.Add(key);
        while (index < before.Count)
            result.Add(before[index++]);
        return [.. result];
    }

    private static Keyframe[] OrderWithMovedKey(
        IReadOnlyList<Keyframe> before, Keyframe key, float second)
        => OrderWithInsertedKey([.. before.Where(candidate =>
            !ReferenceEquals(candidate, key))], key, second);

    private static bool MatchesChildren(AnimationMember parent,
        IReadOnlyList<AnimationMember> expected, object container)
    {
        if (!ReferenceEquals(parent.Children, container) ||
            parent.Children.Count != expected.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
            if (!ReferenceEquals(parent.Children[index], expected[index]))
                return false;
        return true;
    }

    private static bool TryCaptureNodePath(AnimationClip clip,
        AnimationMember target, out Func<bool>? owns)
    {
        owns = null;
        if (clip.RootMember is not { } root)
            return false;
        Stack<(AnimationMember Node,
            List<(AnimationMember Parent, object Container, int Index,
                AnimationMember Child)> Path)> pending = new();
        HashSet<AnimationMember> seen = new(ReferenceEqualityComparer.Instance);
        pending.Push((root, []));
        while (pending.Count > 0)
        {
            var (node, path) = pending.Pop();
            if (!seen.Add(node))
                continue;
            if (ReferenceEquals(node, target))
            {
                var frozen = path.ToArray();
                owns = () =>
                {
                    if (!ReferenceEquals(clip.RootMember, root))
                        return false;
                    foreach (var (parent, container, index, child) in frozen)
                        if (!ReferenceEquals(parent.Children, container) ||
                            (uint)index >= (uint)parent.Children.Count ||
                            !ReferenceEquals(parent.Children[index], child))
                            return false;
                    return true;
                };
                return true;
            }
            for (int index = node.Children.Count - 1; index >= 0; index--)
            {
                AnimationMember child = node.Children[index];
                var childPath = new List<(AnimationMember, object, int,
                    AnimationMember)>(path)
                { (node, node.Children, index, child) };
                pending.Push((child, childPath));
            }
        }
        return false;
    }
}
