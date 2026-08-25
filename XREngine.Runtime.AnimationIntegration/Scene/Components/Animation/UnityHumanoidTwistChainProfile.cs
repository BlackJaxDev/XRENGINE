namespace XREngine.Components.Animation;

/// <summary>
/// Declares the standard roles and Unity HumanDescription distribution used by one limb twist chain.
/// </summary>
public sealed class UnityHumanoidTwistChainProfile
{
    public string Name { get; set; } = string.Empty;
    public EUnityHumanoidAvatarRole ProximalRole { get; set; }
    public EUnityHumanoidAvatarRole DistalRole { get; set; }
    public EUnityHumanoidAvatarRole EndRole { get; set; }
    public float ProximalDistribution { get; set; } = 0.5f;
    public float DistalDistribution { get; set; } = 0.5f;
}
