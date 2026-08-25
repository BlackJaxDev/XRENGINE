namespace XREngine.Components.Animation;

/// <summary>
/// Selects optional post-pose contact correction for animation-driven IK goals.
/// </summary>
public enum EHumanoidContactCompensationMode
{
    /// <summary>Preserve the authored body-relative IK goals exactly.</summary>
    Disabled = 0,

    /// <summary>Prevent authored foot goals from penetrating a configured world-space plane.</summary>
    GroundPlaneFeet,

    /// <summary>Apply the configured ground-plane constraint to authored foot and hand goals.</summary>
    GroundPlaneFeetAndHands,
}
