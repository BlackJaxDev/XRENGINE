namespace XREngine;

/// <summary>
/// Reuse domains used by the engine.
/// </summary>
[Flags]
public enum ERvcReuseDomain
{
    /// <summary>
    /// No reuse domain.
    /// </summary>
    None = 0,
    /// <summary>
    /// Intra-view reuse domain.
    /// </summary>
    IntraView = 1 << 0,
    /// <summary>
    /// Inset-wide reuse domain.
    /// </summary>
    InsetWide = 1 << 1,
    /// <summary>
    /// Stereo reuse domain.
    /// </summary>
    Stereo = 1 << 2,
    /// <summary>
    /// Temporal reuse domain.
    /// </summary>
    Temporal = 1 << 3,
}
