namespace XREngine.Components.Animation;

/// <summary>Describes how the latest authored IK goal sample was handled.</summary>
public enum EHumanoidIKGoalApplicationStatus
{
    None,
    IgnoredByPolicy,
    SkippedUncalibrated,
    AppliedAuthored,
    AppliedWithContactCompensation,
}
