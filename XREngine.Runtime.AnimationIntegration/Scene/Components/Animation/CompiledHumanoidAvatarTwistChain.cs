using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Immutable role-indexed twist-chain data prepared for allocation-free
/// humanoid evaluation.
/// </summary>
internal sealed class CompiledHumanoidAvatarTwistChain
{
    public CompiledHumanoidAvatarTwistChain(
        string name,
        EHumanoidAvatarBoneRole proximalRole,
        EHumanoidAvatarBoneRole distalRole,
        EHumanoidAvatarBoneRole endRole,
        float proximalDistribution,
        float distalDistribution,
        Vector3 proximalRemainderAxisInDistalParent,
        Vector3 distalRemainderAxisInEndParent,
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones)
    {
        Name = name;
        ProximalRole = proximalRole;
        DistalRole = distalRole;
        EndRole = endRole;
        ProximalDistribution = proximalDistribution;
        DistalDistribution = distalDistribution;
        ProximalRemainderAxisInDistalParent = proximalRemainderAxisInDistalParent;
        DistalRemainderAxisInEndParent = distalRemainderAxisInEndParent;
        AuxiliaryBones = auxiliaryBones;
    }

    public string Name { get; }
    public EHumanoidAvatarBoneRole ProximalRole { get; }
    public EHumanoidAvatarBoneRole DistalRole { get; }
    public EHumanoidAvatarBoneRole EndRole { get; }
    public float ProximalDistribution { get; }
    public float DistalDistribution { get; }
    /// <summary>Proximal segment axis expressed in the distal bone's concrete parent frame.</summary>
    public Vector3 ProximalRemainderAxisInDistalParent { get; }
    /// <summary>Distal segment axis expressed in the end bone's concrete parent frame.</summary>
    public Vector3 DistalRemainderAxisInEndParent { get; }
    public CompiledHumanoidAvatarAuxiliaryBone[] AuxiliaryBones { get; }
}
