namespace XREngine.Components.Animation;

/// <summary>
/// Standard character stance and locomotion styles shared across steering snapshots,
/// animation state machine parameters, and procedural locomotion drivers.
/// </summary>
public enum ELocomotionStyle
{
    /// <summary>Upright standing locomotion.</summary>
    Standing = 0,

    /// <summary>Lowered crouched locomotion.</summary>
    Crouched = 1,

    /// <summary>Low-profile prone crawling locomotion.</summary>
    Prone = 2,
}
