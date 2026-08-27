using System.Numerics;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private AnimationQuaternionFloatSlotGroup[] _unityHumanoidScalarQuaternionGroups = [];
    private AnimationQuaternionFloatSlotGroup[] _unityHumanoidScalarQuaternionMirroredGroups = [];
    private AnimationMember[] _unityHumanoidScalarQuaternionXMembers = [];
    private AnimationMember[] _unityHumanoidScalarQuaternionYMembers = [];
    private AnimationMember[] _unityHumanoidScalarQuaternionZMembers = [];
    private AnimationMember[] _unityHumanoidScalarQuaternionWMembers = [];

    internal void PrepareUnityHumanoidScalarQuaternionBindings(AnimationSlotLayout layout)
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
                || !IsUnityHumanoidScalarIKQuaternion(x, y, z, w))
                continue;

            groups.Add(group);
            mirroredGroups.Add(new AnimationQuaternionFloatSlotGroup(
                ResolveUnityHumanoidMirroredFloatSlotIndex(group.XIndex),
                ResolveUnityHumanoidMirroredFloatSlotIndex(group.YIndex),
                ResolveUnityHumanoidMirroredFloatSlotIndex(group.ZIndex),
                ResolveUnityHumanoidMirroredFloatSlotIndex(group.WIndex)));
            xMembers.Add(x);
            yMembers.Add(y);
            zMembers.Add(z);
            wMembers.Add(w);
        }

        _unityHumanoidScalarQuaternionGroups = [.. groups];
        _unityHumanoidScalarQuaternionMirroredGroups = [.. mirroredGroups];
        _unityHumanoidScalarQuaternionXMembers = [.. xMembers];
        _unityHumanoidScalarQuaternionYMembers = [.. yMembers];
        _unityHumanoidScalarQuaternionZMembers = [.. zMembers];
        _unityHumanoidScalarQuaternionWMembers = [.. wMembers];
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

    private void ApplyUnityHumanoidScalarQuaternionCorrections(
        bool hasUnityHumanoidPolicy,
        UnityHumanoidRootMotionPolicy policy,
        float samplePhase)
    {
        for (int i = 0; i < _unityHumanoidScalarQuaternionGroups.Length; i++)
        {
            AnimationQuaternionFloatSlotGroup group = hasUnityHumanoidPolicy && policy.Mirror
                ? _unityHumanoidScalarQuaternionMirroredGroups[i]
                : _unityHumanoidScalarQuaternionGroups[i];
            Quaternion value = ValueStore.ReadQuaternionFloatGroup(group);
            if (hasUnityHumanoidPolicy
                && policy.LoopPose
                && TrySampleScalarQuaternionEndpoints(i, out Quaternion start, out Quaternion end))
            {
                if (policy.Mirror)
                {
                    start = UnityHumanoidMirrorOperator.MirrorRotation(start);
                    end = UnityHumanoidMirrorOperator.MirrorRotation(end);
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

    internal void ClearUnityHumanoidScalarQuaternionBindings()
    {
        _unityHumanoidScalarQuaternionGroups = [];
        _unityHumanoidScalarQuaternionMirroredGroups = [];
        _unityHumanoidScalarQuaternionXMembers = [];
        _unityHumanoidScalarQuaternionYMembers = [];
        _unityHumanoidScalarQuaternionZMembers = [];
        _unityHumanoidScalarQuaternionWMembers = [];
    }

    private bool TrySampleScalarQuaternionEndpoints(
        int bindingIndex,
        out Quaternion start,
        out Quaternion end)
    {
        start = Quaternion.Identity;
        end = Quaternion.Identity;
        if (!TrySampleFloat(
                _unityHumanoidScalarQuaternionXMembers[bindingIndex].Animation!,
                0.0f,
                out float startX)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionYMembers[bindingIndex].Animation!,
                0.0f,
                out float startY)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionZMembers[bindingIndex].Animation!,
                0.0f,
                out float startZ)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionWMembers[bindingIndex].Animation!,
                0.0f,
                out float startW)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionXMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endX)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionYMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endY)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionZMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endZ)
            || !TrySampleFloat(
                _unityHumanoidScalarQuaternionWMembers[bindingIndex].Animation!,
                LengthInSeconds,
                out float endW))
            return false;

        start = NormalizeOrIdentity(new Quaternion(startX, startY, startZ, startW));
        end = NormalizeOrIdentity(new Quaternion(endX, endY, endZ, endW));
        return true;
    }

    private static bool IsUnityHumanoidScalarIKQuaternion(
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
