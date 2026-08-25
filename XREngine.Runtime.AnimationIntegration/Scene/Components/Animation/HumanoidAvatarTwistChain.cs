namespace XREngine.Components.Animation;

/// <summary>
/// Distribution contract for one semantic limb twist chain.
/// </summary>
public sealed class HumanoidAvatarTwistChain
{
    public string Name { get; set; } = string.Empty;
    public EHumanoidAvatarBoneRole ProximalRole { get; set; }
    public EHumanoidAvatarBoneRole DistalRole { get; set; }
    public EHumanoidAvatarBoneRole EndRole { get; set; }
    public float ProximalDistribution { get; set; } = 0.5f;
    public float DistalDistribution { get; set; } = 0.5f;
    public string[] AuxiliaryStructuralSha256 { get; set; } = [];
}
