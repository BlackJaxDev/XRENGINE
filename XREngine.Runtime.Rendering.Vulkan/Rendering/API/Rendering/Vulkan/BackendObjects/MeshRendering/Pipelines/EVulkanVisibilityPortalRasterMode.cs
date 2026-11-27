namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Selects the portal raster pipeline variant for canonical visibility rasterization.
/// </summary>
internal enum EVulkanVisibilityPortalRasterMode : byte
{
    /// <summary>Primary visibility raster with no stencil test or clipping plane.</summary>
    None = 0,

    /// <summary>Portal sub-view with even reflection parity (counter-clockwise front faces) and stencil equality test on bits 2..6.</summary>
    NormalWinding = 1,

    /// <summary>Portal sub-view with odd reflection parity (clockwise front faces) and stencil equality test on bits 2..6.</summary>
    InvertedWinding = 2,
}
