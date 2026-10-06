namespace XREngine;

/// <summary>
/// Selects the render pipeline family for VR eye output. The engine renders
/// only the selected family. A combination with
/// <see cref="EVrViewRenderMode"/> that cannot run submits no XR projection
/// layer and reports a diagnostic. The engine never substitutes another family.
/// </summary>
public enum EVrRenderPipeline
{
    /// <summary>
    /// A plain <c>DefaultRenderPipeline</c> for each eye output. Supported in
    /// every view mode.
    /// </summary>
    Default,

    /// <summary>
    /// The Advanced pipeline family. Supported in sequential and parallel
    /// command-buffer recording view modes.
    /// </summary>
    Advanced,

    /// <summary>
    /// The Retinal Visibility Cache pipeline. Supported in single-pass stereo
    /// and parallel command-buffer recording view modes. The requested RVC mode
    /// must resolve without a fallback.
    /// </summary>
    Rvc,
}
