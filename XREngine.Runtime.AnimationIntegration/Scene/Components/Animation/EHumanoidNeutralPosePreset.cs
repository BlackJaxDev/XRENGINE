namespace XREngine.Components.Animation;

/// <summary>
/// Selects whether native avatar authoring derives a humanoid neutral joint pose.
/// Explicit per-avatar neutral-pose corrections are authored separately.
/// </summary>
public enum EHumanoidNeutralPosePreset
{
    None,
    HumanoidRetargeting,
}
