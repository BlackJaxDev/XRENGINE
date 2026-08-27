using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private AnimSlot[] _unityHumanoidMirroredFloatSlots = [];
    private float[] _unityHumanoidMirroredFloatScales = [];
    private AnimSlot[] _unityHumanoidMirroredVector3Slots = [];
    private AnimSlot[] _unityHumanoidMirroredQuaternionSlots = [];

    /// <summary>
    /// Creates the stable apply binding required by a mirrored humanoid target.
    /// The source binding remains immutable so mixed mirrored and unmirrored
    /// leaves can write different semantic slots in the same blend.
    /// </summary>
    internal static bool TryCreateUnityHumanoidMirroredBinding(
        string sourcePath,
        AnimationMember sourceMember,
        out string mirroredPath,
        out AnimationMember? mirroredMember)
    {
        mirroredPath = string.Empty;
        mirroredMember = null;
        if (!TryGetUnityHumanoidMirrorTarget(
                sourceMember,
                out object? sourceTarget,
                out object? mirroredTarget,
                out _)
            || Equals(sourceTarget, mirroredTarget))
            return false;

        if (!TryReplaceFirstMethodArgument(
                sourcePath,
                sourceMember,
                sourceTarget,
                mirroredTarget,
                out mirroredPath))
            throw new InvalidOperationException(
                $"Could not derive the mirrored animation path for '{sourcePath}'.");

        mirroredMember = sourceMember.CreateMethodArgumentBindingVariant(0, mirroredTarget);
        return true;
    }

    internal void PrepareUnityHumanoidMirrorSlotBindings(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
    {
        _unityHumanoidMirroredFloatSlots = CreateInvalidSlotArray(layout.FloatCount);
        _unityHumanoidMirroredFloatScales = new float[layout.FloatCount];
        Array.Fill(_unityHumanoidMirroredFloatScales, 1.0f);
        _unityHumanoidMirroredVector3Slots = CreateInvalidSlotArray(layout.Vector3Count);
        _unityHumanoidMirroredQuaternionSlots = CreateInvalidSlotArray(layout.QuaternionCount);

        foreach ((string sourcePath, AnimationMember member) in AnimatedCurves)
        {
            if (!member.Slot.IsValid
                || !TryGetUnityHumanoidMirrorTarget(
                    member,
                    out object? sourceTarget,
                    out object? mirroredTarget,
                    out float scalarScale))
                continue;

            string mirroredPath = sourcePath;
            if (!Equals(sourceTarget, mirroredTarget)
                && !TryReplaceFirstMethodArgument(
                    sourcePath,
                    member,
                    sourceTarget,
                    mirroredTarget,
                    out mirroredPath))
                throw new InvalidOperationException(
                    $"Could not derive the mirrored animation path for '{sourcePath}'.");

            if (!slotsByPath.TryGetValue(mirroredPath, out AnimSlot mirroredSlot)
                || mirroredSlot.Type != member.Slot.Type)
                throw new InvalidOperationException(
                    $"The mirrored humanoid binding '{mirroredPath}' has no compatible animation slot.");

            switch (member.Slot.Type)
            {
                case EAnimValueType.Float:
                    _unityHumanoidMirroredFloatSlots[member.Slot.TypeIndex] = mirroredSlot;
                    _unityHumanoidMirroredFloatScales[member.Slot.TypeIndex] = scalarScale;
                    break;
                case EAnimValueType.Vector3:
                    _unityHumanoidMirroredVector3Slots[member.Slot.TypeIndex] = mirroredSlot;
                    break;
                case EAnimValueType.Quaternion:
                    _unityHumanoidMirroredQuaternionSlots[member.Slot.TypeIndex] = mirroredSlot;
                    break;
            }
        }
    }

    private AnimSlot ResolveUnityHumanoidMirroredFloatSlot(
        AnimationMember member,
        bool mirror,
        ref float value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_unityHumanoidMirroredFloatSlots.Length
            || !_unityHumanoidMirroredFloatSlots[source.TypeIndex].IsValid)
            return source;

        value *= _unityHumanoidMirroredFloatScales[source.TypeIndex];
        return _unityHumanoidMirroredFloatSlots[source.TypeIndex];
    }

    private AnimSlot ResolveUnityHumanoidMirroredVector3Slot(
        AnimationMember member,
        bool mirror,
        ref Vector3 value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_unityHumanoidMirroredVector3Slots.Length
            || !_unityHumanoidMirroredVector3Slots[source.TypeIndex].IsValid)
            return source;

        value = UnityHumanoidMirrorOperator.MirrorPosition(value);
        return _unityHumanoidMirroredVector3Slots[source.TypeIndex];
    }

    private AnimSlot ResolveUnityHumanoidMirroredQuaternionSlot(
        AnimationMember member,
        bool mirror,
        ref Quaternion value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_unityHumanoidMirroredQuaternionSlots.Length
            || !_unityHumanoidMirroredQuaternionSlots[source.TypeIndex].IsValid)
            return source;

        value = UnityHumanoidMirrorOperator.MirrorRotation(value);
        return _unityHumanoidMirroredQuaternionSlots[source.TypeIndex];
    }

    private int ResolveUnityHumanoidMirroredFloatSlotIndex(int sourceIndex)
        => (uint)sourceIndex < (uint)_unityHumanoidMirroredFloatSlots.Length
            && _unityHumanoidMirroredFloatSlots[sourceIndex].IsValid
                ? _unityHumanoidMirroredFloatSlots[sourceIndex].TypeIndex
                : sourceIndex;

    private void ClearUnityHumanoidMirrorSlotBindings()
    {
        _unityHumanoidMirroredFloatSlots = [];
        _unityHumanoidMirroredFloatScales = [];
        _unityHumanoidMirroredVector3Slots = [];
        _unityHumanoidMirroredQuaternionSlots = [];
    }

    private static AnimSlot[] CreateInvalidSlotArray(int count)
    {
        if (count <= 0)
            return [];

        var slots = new AnimSlot[count];
        Array.Fill(slots, AnimSlot.Invalid);
        return slots;
    }

    private static bool TryGetUnityHumanoidMirrorTarget(
        AnimationMember member,
        out object? sourceTarget,
        out object? mirroredTarget,
        out float scalarScale)
    {
        sourceTarget = null;
        mirroredTarget = null;
        scalarScale = 1.0f;
        if (member.MemberType != EAnimationMemberType.Method)
            return false;

        if (member.MemberName is "SetValue" or "SetImportedRawValue"
            && TryGetUnityHumanoidMuscleArgument(member, out EHumanoidValue muscle))
        {
            sourceTarget = muscle;
            mirroredTarget = UnityHumanoidMirrorOperator.MirrorMuscle(muscle, out scalarScale);
            return true;
        }

        if (!IsUnityHumanoidIKMember(member)
            || !TryGetUnityHumanoidGoalArgument(member, out ELimbEndEffector goal))
            return false;

        sourceTarget = goal;
        mirroredTarget = UnityHumanoidMirrorOperator.MirrorGoal(goal);
        scalarScale = member.MemberName is "SetAnimatedIKPositionX"
            or "SetAnimatedIKRotationY"
            or "SetAnimatedIKRotationZ"
                ? -1.0f
                : 1.0f;
        return true;
    }

    private static bool TryReplaceFirstMethodArgument(
        string sourcePath,
        AnimationMember member,
        object? sourceArgument,
        object? mirroredArgument,
        out string mirroredPath)
    {
        string sourceText = sourceArgument?.ToString() ?? "<null>";
        int memberIndex = sourcePath.LastIndexOf(member.MemberName, StringComparison.Ordinal);
        int argumentStart = memberIndex < 0
            ? -1
            : memberIndex + member.MemberName.Length + 1;
        if (argumentStart < 0
            || argumentStart + sourceText.Length > sourcePath.Length
            || !sourcePath.AsSpan(argumentStart, sourceText.Length)
                .Equals(sourceText, StringComparison.Ordinal))
        {
            mirroredPath = string.Empty;
            return false;
        }

        int argumentEnd = argumentStart + sourceText.Length;
        if (argumentEnd < sourcePath.Length && sourcePath[argumentEnd] != ':')
        {
            mirroredPath = string.Empty;
            return false;
        }

        mirroredPath = string.Concat(
            sourcePath.AsSpan(0, argumentStart),
            mirroredArgument?.ToString() ?? "<null>",
            sourcePath.AsSpan(argumentEnd));
        return true;
    }
}
