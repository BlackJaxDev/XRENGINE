using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private AnimSlot[] _importedHumanoidMirroredFloatSlots = [];
    private float[] _importedHumanoidMirroredFloatScales = [];
    private AnimSlot[] _importedHumanoidMirroredVector3Slots = [];
    private AnimSlot[] _importedHumanoidMirroredQuaternionSlots = [];

    /// <summary>
    /// Creates the stable apply binding required by a mirrored humanoid target.
    /// The source binding remains immutable so mixed mirrored and unmirrored
    /// leaves can write different semantic slots in the same blend.
    /// </summary>
    internal static bool TryCreateImportedHumanoidMirroredBinding(
        string sourcePath,
        AnimationMember sourceMember,
        out string mirroredPath,
        out AnimationMember? mirroredMember)
    {
        mirroredPath = string.Empty;
        mirroredMember = null;
        if (!TryGetImportedHumanoidMirrorTarget(
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

    internal void PrepareImportedHumanoidMirrorSlotBindings(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
    {
        _importedHumanoidMirroredFloatSlots = CreateInvalidSlotArray(layout.FloatCount);
        _importedHumanoidMirroredFloatScales = new float[layout.FloatCount];
        Array.Fill(_importedHumanoidMirroredFloatScales, 1.0f);
        _importedHumanoidMirroredVector3Slots = CreateInvalidSlotArray(layout.Vector3Count);
        _importedHumanoidMirroredQuaternionSlots = CreateInvalidSlotArray(layout.QuaternionCount);

        foreach ((string sourcePath, AnimationMember member) in AnimatedCurves)
        {
            if (!member.Slot.IsValid
                || !TryGetImportedHumanoidMirrorTarget(
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
                    _importedHumanoidMirroredFloatSlots[member.Slot.TypeIndex] = mirroredSlot;
                    _importedHumanoidMirroredFloatScales[member.Slot.TypeIndex] = scalarScale;
                    break;
                case EAnimValueType.Vector3:
                    _importedHumanoidMirroredVector3Slots[member.Slot.TypeIndex] = mirroredSlot;
                    break;
                case EAnimValueType.Quaternion:
                    _importedHumanoidMirroredQuaternionSlots[member.Slot.TypeIndex] = mirroredSlot;
                    break;
            }
        }
    }

    private AnimSlot ResolveImportedHumanoidMirroredFloatSlot(
        AnimationMember member,
        bool mirror,
        ref float value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_importedHumanoidMirroredFloatSlots.Length
            || !_importedHumanoidMirroredFloatSlots[source.TypeIndex].IsValid)
            return source;

        value *= _importedHumanoidMirroredFloatScales[source.TypeIndex];
        return _importedHumanoidMirroredFloatSlots[source.TypeIndex];
    }

    private AnimSlot ResolveImportedHumanoidMirroredVector3Slot(
        AnimationMember member,
        bool mirror,
        ref Vector3 value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_importedHumanoidMirroredVector3Slots.Length
            || !_importedHumanoidMirroredVector3Slots[source.TypeIndex].IsValid)
            return source;

        value = ImportedHumanoidMirrorOperator.MirrorPosition(value);
        return _importedHumanoidMirroredVector3Slots[source.TypeIndex];
    }

    private AnimSlot ResolveImportedHumanoidMirroredQuaternionSlot(
        AnimationMember member,
        bool mirror,
        ref Quaternion value)
    {
        AnimSlot source = member.Slot;
        if (!mirror
            || (uint)source.TypeIndex >= (uint)_importedHumanoidMirroredQuaternionSlots.Length
            || !_importedHumanoidMirroredQuaternionSlots[source.TypeIndex].IsValid)
            return source;

        value = ImportedHumanoidMirrorOperator.MirrorRotation(value);
        return _importedHumanoidMirroredQuaternionSlots[source.TypeIndex];
    }

    private int ResolveImportedHumanoidMirroredFloatSlotIndex(int sourceIndex)
        => (uint)sourceIndex < (uint)_importedHumanoidMirroredFloatSlots.Length
            && _importedHumanoidMirroredFloatSlots[sourceIndex].IsValid
                ? _importedHumanoidMirroredFloatSlots[sourceIndex].TypeIndex
                : sourceIndex;

    private void ClearImportedHumanoidMirrorSlotBindings()
    {
        _importedHumanoidMirroredFloatSlots = [];
        _importedHumanoidMirroredFloatScales = [];
        _importedHumanoidMirroredVector3Slots = [];
        _importedHumanoidMirroredQuaternionSlots = [];
    }

    private static AnimSlot[] CreateInvalidSlotArray(int count)
    {
        if (count <= 0)
            return [];

        var slots = new AnimSlot[count];
        Array.Fill(slots, AnimSlot.Invalid);
        return slots;
    }

    private static bool TryGetImportedHumanoidMirrorTarget(
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
            && TryGetImportedHumanoidMuscleArgument(member, out EHumanoidValue muscle))
        {
            sourceTarget = muscle;
            mirroredTarget = ImportedHumanoidMirrorOperator.MirrorMuscle(muscle, out scalarScale);
            return true;
        }

        if (!IsImportedHumanoidIKMember(member)
            || !TryGetImportedHumanoidGoalArgument(member, out ELimbEndEffector goal))
            return false;

        sourceTarget = goal;
        mirroredTarget = ImportedHumanoidMirrorOperator.MirrorGoal(goal);
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
