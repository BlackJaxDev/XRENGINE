namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The priority band of a geometry page request. A load never evicts a page that was loaded for a
/// higher band.
/// </summary>
public enum EMeshletPageRequestBand : byte
{
    /// <summary>The request came from a draw that only failed the occlusion test.</summary>
    Occluded = 0,
    /// <summary>The page is in the ideal cut at the prefetch threshold, but the drawn cover is already good enough.</summary>
    Prefetch = 1,
    /// <summary>The drawn cover has an error above the pixel threshold, so the missing page shows now.</summary>
    Foreground = 2,
}
