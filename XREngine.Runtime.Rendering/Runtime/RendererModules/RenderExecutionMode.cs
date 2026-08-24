namespace XREngine.Rendering;

/// <summary>
/// Identifies the presentation contract exercised by a renderer run. These values are
/// deliberately distinct: a presentationless result is not comparable to a WSI or XR result.
/// </summary>
public enum RenderExecutionMode
{
    Component,
    Presentationless,
    HeadlessWsi,
    DesktopWsi,
    OpenXr,
    /// <summary>Browser-owned HTML canvas or offscreen-canvas presentation.</summary>
    BrowserCanvas,
}
