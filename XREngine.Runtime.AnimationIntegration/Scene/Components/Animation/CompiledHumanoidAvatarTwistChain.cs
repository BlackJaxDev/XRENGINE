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
        CompiledHumanoidAvatarAuxiliaryBone[] auxiliaryBones)
    {
        Name = name;
        ProximalRole = proximalRole;
        DistalRole = distalRole;
        EndRole = endRole;
        ProximalDistribution = proximalDistribution;
        DistalDistribution = distalDistribution;
        AuxiliaryBones = auxiliaryBones;
    }

    public string Name { get; }
    public EHumanoidAvatarBoneRole ProximalRole { get; }
    public EHumanoidAvatarBoneRole DistalRole { get; }
    public EHumanoidAvatarBoneRole EndRole { get; }
    public float ProximalDistribution { get; }
    public float DistalDistribution { get; }
    public CompiledHumanoidAvatarAuxiliaryBone[] AuxiliaryBones { get; }
}
