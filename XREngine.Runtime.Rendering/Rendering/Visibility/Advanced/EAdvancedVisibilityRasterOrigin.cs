namespace XREngine.Rendering;

/// <summary>
/// Identifies which visibility phase produced the authoritative pixel.
/// </summary>
public enum EAdvancedVisibilityRasterOrigin : uint
{
    Early = 0u,
    Late = 1u,
}
