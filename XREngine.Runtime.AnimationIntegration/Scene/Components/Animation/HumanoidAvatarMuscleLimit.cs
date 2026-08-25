namespace XREngine.Components.Animation;

/// <summary>
/// Resolved asymmetric range for one normalized humanoid muscle channel.
/// </summary>
public sealed class HumanoidAvatarMuscleLimit
{
    public EHumanoidValue Muscle { get; set; }
    public float NegativeDegrees { get; set; }
    public float PositiveDegrees { get; set; }
}
