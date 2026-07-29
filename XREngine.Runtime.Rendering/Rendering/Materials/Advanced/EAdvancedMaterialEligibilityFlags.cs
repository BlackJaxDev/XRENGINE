namespace XREngine.Rendering;

/// <summary>
/// Explicit shading-path eligibility published with each material row.
/// </summary>
[Flags]
public enum EAdvancedMaterialEligibilityFlags : uint
{
    None = 0,
    NativeOpaque = 1u << 0,
    NativeMasked = 1u << 1,
    LateTransparent = 1u << 2,
    LateRefractive = 1u << 3,
    Unlit = 1u << 4,
    Unsupported = 1u << 31,
}
