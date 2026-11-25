namespace XREngine.Data.Rendering;

/// <summary>
/// Selects the GPU texture format used by the on-demand Dual-Kawase UI backdrop blur pyramid.
/// </summary>
public enum EUiBackdropBlurFormat
{
    /// <summary>
    /// 8-bit normalized RGBA (lowest memory bandwidth).
    /// </summary>
    Rgba8 = 0,

    /// <summary>
    /// 16-bit half-float RGBA (higher precision on dark scenes with minimal banding).
    /// </summary>
    Rgba16f = 1,

    /// <summary>
    /// Packed 11/11/10-bit unsigned float RGB (half the memory of Rgba16f with float precision).
    /// </summary>
    R11fG11fB10f = 2,
}
