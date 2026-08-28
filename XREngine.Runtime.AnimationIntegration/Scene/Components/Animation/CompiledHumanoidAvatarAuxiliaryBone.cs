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
        int index,
        EHumanoidAvatarAuxiliaryBoneKind kind,
        EHumanoidAvatarBoneRole parentRole,
        SceneNode node,
        Matrix4x4 neutralLocalTransform,
        Vector3 neutralScale,
        Quaternion neutralRotation,
        Vector3 neutralTranslation,
        Vector3 localAxis,
        float distributionWeight,
        string structuralSha256)
    {
        Index = index;
        Kind = kind;
        ParentRole = parentRole;
        Node = node;
        NeutralLocalTransform = neutralLocalTransform;
        NeutralScale = neutralScale;
        NeutralRotation = neutralRotation;
        NeutralTranslation = neutralTranslation;
        LocalAxis = localAxis;
        DistributionWeight = distributionWeight;
        StructuralSha256 = structuralSha256;
    }

    public int Index { get; }
    public EHumanoidAvatarAuxiliaryBoneKind Kind { get; }
    public EHumanoidAvatarBoneRole ParentRole { get; }
    public SceneNode Node { get; }
    public Matrix4x4 NeutralLocalTransform { get; }
    public Vector3 NeutralScale { get; }
    public Quaternion NeutralRotation { get; }
    public Vector3 NeutralTranslation { get; }
    public Vector3 LocalAxis { get; }
    public float DistributionWeight { get; }
    public string StructuralSha256 { get; }
}
