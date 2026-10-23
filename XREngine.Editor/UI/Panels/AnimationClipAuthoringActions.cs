using System.Collections;
using System.Numerics;
using XREngine.Animation;
using XREngine.Editor.UI;

namespace XREngine.Editor;

/// <summary>Applies clip key edits through the editor undo and dirty paths.</summary>
internal static class AnimationClipAuthoringActions
{
    public static bool TryAddKey(AnimationClip clip, AnimationMember member, float second, out Keyframe? key)
    {
        key = null;
        if (!ContainsMember(clip, member) || member.Animation is not { } animation || !float.IsFinite(second))
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
        bool applied = Apply(clip, "Add Animation Key",
            () => Add(animation, addedKey),
            () => { if (addedKey.OwningTrack is not null) addedKey.Remove(); },
            () => ContainsKey(animation, addedKey),
            () => !ContainsKey(animation, addedKey));
        if (!applied)
            key = null;
        return applied;
    }

    public static bool RemoveKey(AnimationClip clip, AnimationMember member, Keyframe key)
    {
        if (!ContainsMember(clip, member) || member.Animation is not { } animation ||
            !ContainsKey(animation, key))
            return false;
        return Apply(clip, "Remove Animation Key",
            () => { if (key.OwningTrack is not null) key.Remove(); },
            () => { if (!ContainsKey(animation, key)) Add(animation, key); },
            () => !ContainsKey(animation, key),
            () => ContainsKey(animation, key));
    }

    public static bool SetKeyTime(AnimationClip clip, Keyframe key, float second)
    {
        if (!float.IsFinite(second) || !ContainsKey(clip, key))
            return false;
        float next = Math.Clamp(second, 0.0f, MathF.Max(0.0f, clip.LengthInSeconds));
        float previous = key.Second;
        if (MathF.Abs(previous - next) < 0.000001f)
            return false;
        int previousFrame = key.AuthoredFrameIndex;
        return Apply(clip, "Move Animation Key",
            () => key.Second = next,
            () => { key.Second = previous; key.AuthoredFrameIndex = previousFrame; },
            () => MathF.Abs(key.Second - next) < 0.000001f,
            () => MathF.Abs(key.Second - previous) < 0.000001f && key.AuthoredFrameIndex == previousFrame);
    }

    public static bool SetKeyValue(AnimationClip clip, Keyframe key, object value)
    {
        if (!ContainsKey(clip, key) || value is null)
            return false;
        if (key is IPlanarKeyframe planar && value.GetType() == key.ValueType)
        {
            object previous = planar.OutValue;
            if (Equals(previous, value))
                return false;
            return Apply(clip, "Edit Animation Key Value",
                () => planar.OutValue = value, () => planar.OutValue = previous,
                () => Equals(planar.OutValue, value), () => Equals(planar.OutValue, previous));
        }
        if (key is QuaternionKeyframe quaternion && value is Quaternion rotation)
        {
            Quaternion previous = quaternion.OutValue;
            if (previous == rotation)
                return false;
            return Apply(clip, "Edit Animation Key Value",
                () => quaternion.OutValue = rotation, () => quaternion.OutValue = previous,
                () => quaternion.OutValue == rotation, () => quaternion.OutValue == previous);
        }
        if (key is BoolKeyframe boolean && value is bool next)
        {
            bool previous = boolean.Value;
            if (previous == next)
                return false;
            return Apply(clip, "Edit Animation Key Value",
                () => boolean.Value = next, () => boolean.Value = previous,
                () => boolean.Value == next, () => boolean.Value == previous);
        }
        return false;
    }

    public static bool RemoveMember(AnimationClip clip, AnimationMember member)
    {
        AnimationMember? root = clip.RootMember;
        if (root is null)
            return false;
        if (ReferenceEquals(root, member))
        {
            return Apply(clip, "Remove Animation Path",
                () => { clip.RootMember = null; UpdateClipStats(clip); },
                () => { clip.RootMember = root; UpdateClipStats(clip); },
                () => clip.RootMember is null,
                () => ReferenceEquals(clip.RootMember, root));
        }
        if (!TryFindParent(root, member, out AnimationMember? parent, out int index) || parent is null)
            return false;
        return Apply(clip, "Remove Animation Path",
            () => { if (parent.Children.Contains(member)) parent.Children.Remove(member); UpdateClipStats(clip); },
            () =>
            {
                if (!parent.Children.Contains(member))
                    parent.Children.Insert(Math.Clamp(index, 0, parent.Children.Count), member);
                UpdateClipStats(clip);
            },
            () => !parent.Children.Contains(member),
            () => parent.Children.Contains(member));
    }

    internal static bool Apply(AnimationClip clip, string description, Action forward, Action reverse,
        Func<bool> isApplied, Func<bool> isRestored)
    {
        void RunTracked(Action action)
        {
            try { action(); }
            finally { EditorMutationDirtyState.Mark(clip); }
        }

        using var interaction = Undo.BeginUserInteraction();
        using var change = Undo.BeginChange(description);
        Undo.RecordStructuralChange(description,
            () => RunTracked(reverse),
            () => RunTracked(forward));
        try
        {
            RunTracked(forward);
            if (!isApplied())
                throw new InvalidOperationException("Animation edit did not reach its expected state.");
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                RunTracked(reverse);
            }
            catch (Exception rollbackException)
            {
                Debug.LogException(rollbackException, $"Could not reverse '{description}'.");
            }
            bool restored;
            try { restored = isRestored(); }
            catch (Exception checkException)
            {
                restored = false;
                Debug.LogException(checkException, $"Could not verify '{description}' rollback.");
            }
            if (restored)
                change.Cancel();
            else
                Debug.UI($"[NativeAnimation] '{description}' partially applied; Undo was retained for recovery.");
            Debug.LogException(exception, $"Failed animation edit '{description}'.");
            return false;
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
        if (key.OwningTrack is null || animation is not IEnumerable keys)
            return false;
        foreach (object? candidate in keys)
            if (ReferenceEquals(candidate, key))
                return true;
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
