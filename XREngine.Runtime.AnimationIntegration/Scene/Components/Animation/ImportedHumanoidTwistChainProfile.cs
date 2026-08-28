namespace XREngine.Components.Animation;

/// <summary>
/// Declares the standard roles and Unity HumanDescription distribution used by one limb twist chain.
/// </summary>
public sealed class ImportedHumanoidTwistChainProfile
{
    public string Name { get; set; } = string.Empty;
    public EHumanoidAvatarRole ProximalRole { get; set; }
    public EHumanoidAvatarRole DistalRole { get; set; }
    public EHumanoidAvatarRole EndRole { get; set; }
    public float ProximalDistribution { get; set; } = 0.5f;
    public float DistalDistribution { get; set; } = 0.5f;
}
