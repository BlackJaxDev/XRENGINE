namespace XREngine.Components;

/// <summary>
/// Framing preset for a third-person camera boom controlled by <see cref="ThirdPersonCameraRigComponent"/>.
/// </summary>
public enum EThirdPersonCameraViewMode
{
    /// <summary>Centered view positioned above the character's head with adjustable zoom distance.</summary>
    Centered = 0,

    /// <summary>Close, lowered over-the-left-shoulder view aligned near chest level.</summary>
    LeftShoulder = 1,

    /// <summary>Close, lowered over-the-right-shoulder view aligned near chest level.</summary>
    RightShoulder = 2,
}
