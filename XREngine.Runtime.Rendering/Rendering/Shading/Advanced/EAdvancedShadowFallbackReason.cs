namespace XREngine.Rendering;

/// <summary>
/// Per-pixel native shadow sampling status, shared with StandardShadow.glslinc.
/// Outside coverage and deliberate stale reuse remain distinguishable from missing resources.
/// </summary>
public enum EAdvancedShadowFallbackReason : uint
{
    None = 0,
    MissingRecord = 1,
    StaleHandle = 2,
    NotResident = 3,
    Unsupported = 4,
    OutsideCoverage = 5,
    StaleTile = 6,
}