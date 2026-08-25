namespace XREngine.Components.Animation;

/// <summary>
/// Maps one canonical humanoid role to the source avatar transform Unity assigned.
/// </summary>
public sealed class UnityHumanoidAvatarRoleProfile
{
    public EUnityHumanoidAvatarRole Role { get; set; }
    public string HumanName { get; set; } = string.Empty;
    public string TransformName { get; set; } = string.Empty;
    public bool Required { get; set; }
}
