namespace XREngine.Components.VR;

/// <summary>
/// Selects when a <see cref="VrHeadChopTargetBone"/> scale override applies.
/// </summary>
public enum EVrHeadChopApplyCondition
{
    /// <summary>Applies in both VR and desktop modes when head hiding is active.</summary>
    AlwaysApply = 0,

    /// <summary>Applies only when VR mode is active.</summary>
    VrOnly = 1,

    /// <summary>Applies only when desktop mode is active.</summary>
    DesktopOnly = 2,
}
