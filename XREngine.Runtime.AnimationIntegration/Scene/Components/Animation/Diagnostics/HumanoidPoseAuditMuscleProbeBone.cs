namespace XREngine.Components.Animation;

/// <summary>
/// Stores one bone's bind-local response relative to the avatar's zero-muscle
/// pose for an isolated muscle probe.
/// </summary>
public sealed class HumanoidPoseAuditMuscleProbeBone
{
    public string Name { get; set; } = string.Empty;
    public HumanoidPoseAuditQuaternion NegativePoseDeltaFromNeutralRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditQuaternion PositivePoseDeltaFromNeutralRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
}
