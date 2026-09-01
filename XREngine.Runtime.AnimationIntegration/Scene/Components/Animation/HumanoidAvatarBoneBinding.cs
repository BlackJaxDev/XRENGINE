using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Stable serialized binding for one semantic humanoid role. Scene-node
/// references remain compiled runtime bindings on <see cref="HumanoidComponent"/>.
/// </summary>
public sealed class HumanoidAvatarBoneBinding
{
    public EHumanoidAvatarBoneRole Role { get; set; }
    public bool Required { get; set; }
    public EHumanoidAvatarBoneRole? ParentRole { get; set; }
    public string NodePath { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string StructuralAddress { get; set; } = string.Empty;
    public string StructuralSha256 { get; set; } = string.Empty;
    public string NeutralPoseSha256 { get; set; } = string.Empty;
    public Matrix4x4 NeutralLocalTransform { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 NeutralWorldTransform { get; set; } = Matrix4x4.Identity;
    /// <summary>
    /// Unity-compatible canonicalized bind transform. This is derived from the
    /// complete mapped hierarchy, rather than copied from importer pivot data,
    /// and is used only to author semantic joint frames.
    /// </summary>
    public Matrix4x4 CanonicalWorldTransform { get; set; } = Matrix4x4.Identity;
    public Vector3 NeutralLocalPosition { get; set; }
    public Quaternion NeutralLocalRotation { get; set; } = Quaternion.Identity;
    /// <summary>Canonical local bind rotation authored from the normalized hierarchy.</summary>
    public Quaternion CanonicalLocalRotation { get; set; } = Quaternion.Identity;
    public Vector3 NeutralLocalScale { get; set; } = Vector3.One;
    public Quaternion CanonicalPoseCorrection { get; set; } = Quaternion.Identity;
    public Quaternion PreRotation { get; set; } = Quaternion.Identity;
    public Quaternion PostRotation { get; set; } = Quaternion.Identity;
    public EHumanoidAvatarRotationOrder RotationOrder { get; set; } = EHumanoidAvatarRotationOrder.ZXY;
    /// <summary>
    /// Whether this role may consume avatar-space translation channels. Unity
    /// normally restricts translation to the Body/Hips path unless the avatar's
    /// HumanDescription enables translation degrees of freedom.
    /// </summary>
    public bool HasTranslationDoF { get; set; }
    public HumanoidAvatarJointLimit JointLimit { get; set; } = new();
    public BoneAxisMapping AxisMapping { get; set; } = BoneAxisMapping.Default;
    public bool HasAxisMapping { get; set; }
    public EHumanoidAvatarMappingSource MappingSource { get; set; }
    public float Confidence { get; set; }
    public float ImportedMetadataScore { get; set; }
    public float TopologyScore { get; set; }
    public float GeometryScore { get; set; }
    public float AxisScore { get; set; }
    public float SymmetryScore { get; set; }
    public float AliasScore { get; set; }
    public string MappingEvidence { get; set; } = string.Empty;
    public bool Locked { get; set; }
}
