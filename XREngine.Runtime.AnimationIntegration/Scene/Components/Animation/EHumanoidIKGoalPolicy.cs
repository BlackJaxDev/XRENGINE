namespace XREngine.Components.Animation;

/// <summary>
/// Controls how animation-driven IK goal channels are applied at runtime.
/// </summary>
public enum EHumanoidIKGoalPolicy
{
    /// <summary>Ignore IK goal channels from animation clips.</summary>
    Ignore = 0,

    /// <summary>Apply authored goals only when the avatar mapping is calibrated.</summary>
    ApplyIfCalibrated,

    /// <summary>Apply authored goals even when calibration confidence is unavailable.</summary>
    AlwaysApply,
}
