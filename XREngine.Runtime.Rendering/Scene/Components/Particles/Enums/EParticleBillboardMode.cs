namespace XREngine.Scene.Components.Particles.Enums;

public enum EParticleBillboardMode
{
    /// <summary>
    /// Particles face the camera.
    /// </summary>
    ViewFacing,

    /// <summary>
    /// Particles face the camera but maintain vertical axis.
    /// </summary>
    ViewFacingVertical,

    /// <summary>
    /// Particles align to velocity direction.
    /// </summary>
    VelocityAligned,

    /// <summary>
    /// Particles use stretched billboards based on velocity.
    /// </summary>
    StretchedBillboard,

    /// <summary>
    /// Particles maintain world space orientation.
    /// </summary>
    WorldSpace,

    /// <summary>
    /// Particles maintain local space orientation.
    /// </summary>
    LocalSpace
}
