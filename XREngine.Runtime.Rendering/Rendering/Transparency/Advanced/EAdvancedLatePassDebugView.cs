namespace XREngine.Rendering;

/// <summary>
/// Diagnostic visualization modes for transparency, special passes, and temporal history.
/// </summary>
public enum EAdvancedLatePassDebugView : uint
{
    Disabled = 0u,
    SceneColorSnapshot = 1u,
    OitAccumulation = 2u,
    OitRevealage = 3u,
    RefractionMask = 4u,
    VolumetricFog = 5u,
    MotionVectors = 6u,
    ReactiveMask = 7u,
    HistoryValidity = 8u,
}
