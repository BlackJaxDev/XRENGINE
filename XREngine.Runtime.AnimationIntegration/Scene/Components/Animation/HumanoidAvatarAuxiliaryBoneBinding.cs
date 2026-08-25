using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Stable binding for a non-semantic twist, roll, translation, or helper bone.
/// </summary>
public sealed class HumanoidAvatarAuxiliaryBoneBinding
{
    public EHumanoidAvatarAuxiliaryBoneKind Kind { get; set; }
    public EHumanoidAvatarBoneRole ParentRole { get; set; }
    public string NodePath { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string StructuralAddress { get; set; } = string.Empty;
    public string StructuralSha256 { get; set; } = string.Empty;
    public Matrix4x4 NeutralLocalTransform { get; set; } = Matrix4x4.Identity;
    public Vector3 LocalAxis { get; set; } = Vector3.UnitY;
    public float DistributionWeight { get; set; }
    public bool Locked { get; set; }
}
