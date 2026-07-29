namespace XREngine.Data.Rendering;

/// <summary>
/// Identifies the output owned by a render-pipeline request.
/// </summary>
public enum ERenderPipelinePurpose
{
    /// <summary>
    /// A mono or stereo scene view presented through a desktop window.
    /// </summary>
    DesktopScene = 0,

    /// <summary>
    /// An OpenXR eye output, rendered either per-eye or with true single-pass stereo.
    /// </summary>
    OpenXrEye,

    /// <summary>
    /// An offscreen scene capture such as a light probe, mirror, or impostor.
    /// </summary>
    OffscreenCapture,
}
