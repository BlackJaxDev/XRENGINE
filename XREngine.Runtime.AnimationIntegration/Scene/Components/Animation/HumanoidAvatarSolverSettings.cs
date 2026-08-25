namespace XREngine.Components.Animation;

/// <summary>
/// Avatar-specific humanoid solver parameters corresponding to Unity's public
/// HumanDescription contract.
/// </summary>
public class HumanoidAvatarSolverSettings
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
