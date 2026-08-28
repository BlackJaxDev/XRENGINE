using System.Numerics;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private AnimationQuaternionFloatSlotGroup[] _importedHumanoidScalarQuaternionGroups = [];
    private AnimationQuaternionFloatSlotGroup[] _importedHumanoidScalarQuaternionMirroredGroups = [];
    private AnimationMember[] _importedHumanoidScalarQuaternionXMembers = [];
    private AnimationMember[] _importedHumanoidScalarQuaternionYMembers = [];
    private AnimationMember[] _importedHumanoidScalarQuaternionZMembers = [];
    private AnimationMember[] _importedHumanoidScalarQuaternionWMembers = [];

    internal void PrepareImportedHumanoidScalarQuaternionBindings(AnimationSlotLayout layout)
    {
        List<AnimationQuaternionFloatSlotGroup> groups = [];
        List<AnimationQuaternionFloatSlotGroup> mirroredGroups = [];
        List<AnimationMember> xMembers = [];
        List<AnimationMember> yMembers = [];
        List<AnimationMember> zMembers = [];
        List<AnimationMember> wMembers = [];
        AnimationQuaternionFloatSlotGroup[] layoutGroups = layout.QuaternionFloatGroups;
        for (int groupIndex = 0; groupIndex < layoutGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = layoutGroups[groupIndex];
            AnimationMember? x = FindFloatMember(group.XIndex);
            AnimationMember? y = FindFloatMember(group.YIndex);
            AnimationMember? z = FindFloatMember(group.ZIndex);
            AnimationMember? w = FindFloatMember(group.WIndex);
            if (x is null || y is null || z is null || w is null
                || !IsImportedHumanoidScalarIKQuaternion(x, y, z, w))
                continue;

            groups.Add(group);
            mirroredGroups.Add(new AnimationQuaternionFloatSlotGroup(
                ResolveImportedHumanoidMirroredFloatSlotIndex(group.XIndex),
                ResolveImportedHumanoidMirroredFloatSlotIndex(group.YIndex),
                ResolveImportedHumanoidMirroredFloatSlotIndex(group.ZIndex),
                ResolveImportedHumanoidMirroredFloatSlotIndex(group.WIndex)));
            xMembers.Add(x);
            yMembers.Add(y);
            zMembers.Add(z);
            wMembers.Add(w);
        }

        _importedHumanoidScalarQuaternionGroups = [.. groups];
        _importedHumanoidScalarQuaternionMirroredGroups = [.. mirroredGroups];
        _importedHumanoidScalarQuaternionXMembers = [.. xMembers];
        _importedHumanoidScalarQuaternionYMembers = [.. yMembers];
        _importedHumanoidScalarQuaternionZMembers = [.. zMembers];
        _importedHumanoidScalarQuaternionWMembers = [.. wMembers];
    }

    private AnimationMember? FindFloatMember(int slotIndex)
    {
        AnimationMember[] members = AnimatedMembersArray;
        for (int i = 0; i < members.Length; i++)
        {
            AnimationMember member = members[i];
            if (member.Slot.Type == EAnimValueType.Float
                && member.Slot.TypeIndex == slotIndex)
                return member;
        }
        return null;
    }

    private void ApplyImportedHumanoidScalarQuaternionCorrections(
        bool hasImportedHumanoidPolicy,
        ImportedHumanoidRootMotionPolicy policy,
        float samplePhase)
    {
        for (int i = 0; i < _importedHumanoidScalarQuaternionGroups.Length; i++)
        {
            AnimationQuaternionFloatSlotGroup group = hasImportedHumanoidPolicy && policy.Mirror
                ? _importedHumanoidScalarQuaternionMirroredGroups[i]
                : _importedHumanoidScalarQuaternionGroups[i];
            Quaternion value = ValueStore.ReadQuaternionFloatGroup(group);
            if (hasImportedHumanoidPolicy
                && policy.LoopPose
                && TrySampleScalarQuaternionEndpoints(i, out Quaternion start, out Quaternion end))
            {
                if (policy.Mirror)
                {
                    start = ImportedHumanoidMirrorOperator.MirrorRotation(start);
                    end = ImportedHumanoidMirrorOperator.MirrorRotation(end);
                }
                if (Quaternion.Dot(start, end) < 0.0f)
                    end = new Quaternion(-end.X, -end.Y, -end.Z, -end.W);
                Quaternion endpointCorrection = Quaternion.Normalize(
                    Quaternion.Inverse(end) * start);
                Quaternion correction = Quaternion.Slerp(
                    Quaternion.Identity,
                    endpointCorrection,
                    samplePhase);
                value = Quaternion.Normalize(value * correction);
            }
            ValueStore.WriteQuaternionFloatGroup(group, value);
        }
    }

    internal void ClearImportedHumanoidScalarQuaternionBindings()
    {
        _importedHumanoidScalarQuaternionGroups = [];
        _importedHumanoidScalarQuaternionMirroredGroups = [];
        _importedHumanoidScalarQuaternionXMembers = [];
        _importedHumanoidScalarQuaternionYMembers = [];
        _importedHumanoidScalarQuaternionZMembers = [];
        _importedHumanoidScalarQuaternionWMembers = [];
    }

    private bool TrySampleScalarQuaternionEndpoints(
        int bindingIndex,
        out Quaternion start,
        out Quaternion end)
    {
        start = Quaternion.Identity;
        end = Quaternion.Identity;
        if (!TrySampleFloat(
                _importedHumanoidScalarQuaternionXMembers[bindingIndex].Animation!,
                0.0f,
                out float startX)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionYMembers[bindingIndex].Animation!,
                0.0f,
                out float startY)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionZMembers[bindingIndex].Animation!,
                0.0f,
                out float startZ)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionWMembers[bindingIndex].Animation!,
                0.0f,
                out float startW)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionXMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endX)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionYMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endY)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionZMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endZ)
            || !TrySampleFloat(
                _importedHumanoidScalarQuaternionWMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endW))
            return false;

        start = NormalizeOrIdentity(new Quaternion(startX, startY, startZ, startW));
        end = NormalizeOrIdentity(new Quaternion(endX, endY, endZ, endW));
        return true;
    }

    private static bool IsImportedHumanoidScalarIKQuaternion(
        AnimationMember x,
        AnimationMember y,
        AnimationMember z,
        AnimationMember w)
        => x.MemberName == "SetAnimatedIKRotationX"
        && y.MemberName == "SetAnimatedIKRotationY"
        && z.MemberName == "SetAnimatedIKRotationZ"
        && w.MemberName == "SetAnimatedIKRotationW";

    private static Quaternion NormalizeOrIdentity(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() > 1.0e-12f
                ? Quaternion.Normalize(value)
                : Quaternion.Identity;
}
