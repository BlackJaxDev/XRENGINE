namespace XREngine.Components.Lights;

/// <summary>
/// Controls native opaque shading rate inside an in-place stencil mirror when
/// multisample visibility (MSAA) is active.
/// </summary>
public enum EMirrorMsaaShadingMode
{
    /// <summary>
    /// Shades the representative sample once per pixel inside the mirror and reuses
    /// that radiance across matching samples of the same portal view.
    /// </summary>
    SingleSample = 0,

    /// <summary>
    /// Runs full per-sample native opaque shading for every MSAA sample inside the mirror.
    /// </summary>
    PerSample = 1,
}
