namespace XREngine.Components.Animation;

/// <summary>
/// Unity HumanDescription solver parameters exported with a humanoid avatar.
/// </summary>
public sealed class UnityHumanoidAvatarDescription
{
    public float UpperArmTwist { get; set; } = 0.5f;
    public float LowerArmTwist { get; set; } = 0.5f;
    public float UpperLegTwist { get; set; } = 0.5f;
    public float LowerLegTwist { get; set; } = 0.5f;
    public float ArmStretch { get; set; } = 0.05f;
    public float LegStretch { get; set; } = 0.05f;
    public float FeetSpacing { get; set; }
    public bool HasTranslationDoF { get; set; }
}
