namespace XREngine.Rendering;

/// <summary>
/// Bits that visibility preparation adds to the raster state class of a payload. The raster
/// state class is part of the indirect range key, so a bit splits its draws into their own ranges.
/// </summary>
public static class AdvancedVisibilityRasterStateBits
{
    /// <summary>
    /// The range writes fragment depth (parallax occlusion mapping). It uses masked coverage and
    /// the depth-writing masked raster programs, which disable early depth testing for this range only.
    /// </summary>
    public const uint FragmentDepth = 1u << 31;

    /// <summary>True when <paramref name="rasterStateClass"/> carries <see cref="FragmentDepth"/>.</summary>
    public static bool WritesFragmentDepth(uint rasterStateClass)
        => (rasterStateClass & FragmentDepth) != 0u;
}
