namespace XREngine;

/// <summary>
/// Foveal anti-aliasing paths used by the engine.
/// </summary>
public enum ERvcFovealAntiAliasingPath
{
    /// <summary>
    /// Visibility edge anti-aliasing path.
    /// </summary>
    VisibilityEdgeAA,
    /// <summary>
    /// Foveated temporal anti-aliasing fallback path.
    /// </summary>
    FoveatedTaaFallback,
    /// <summary>
    /// Disabled anti-aliasing path.
    /// </summary>
    Disabled,
}
