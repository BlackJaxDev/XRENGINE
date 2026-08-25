using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

/// <summary>
/// Immutable runtime binding for one non-semantic twist, roll, helper, or
/// translation transform in a finalized humanoid avatar.
/// </summary>
internal sealed class CompiledHumanoidAvatarAuxiliaryBone
{
    public CompiledHumanoidAvatarAuxiliaryBone(
        EHumanoidAvatarAuxiliaryBoneKind kind,
        EHumanoidAvatarBoneRole parentRole,
        SceneNode node,
        Matrix4x4 neutralLocalTransform,
        Vector3 localAxis,
        float distributionWeight,
        string structuralSha256)
    {
        Kind = kind;
        ParentRole = parentRole;
        Node = node;
        NeutralLocalTransform = neutralLocalTransform;
        LocalAxis = localAxis;
        DistributionWeight = distributionWeight;
        StructuralSha256 = structuralSha256;
    }

    public EHumanoidAvatarAuxiliaryBoneKind Kind { get; }
    public EHumanoidAvatarBoneRole ParentRole { get; }
    public SceneNode Node { get; }
    public Matrix4x4 NeutralLocalTransform { get; }
    public Vector3 LocalAxis { get; }
    public float DistributionWeight { get; }
    public string StructuralSha256 { get; }
}
