namespace XREngine;

/// <summary>
/// Specifies the different implementation paths for rendering VR views.
/// </summary>
public enum EVrViewRenderImplementationPath
{
    /// <summary>
    /// Indicates that the VR view render implementation path is not supported.
    /// </summary>
    Unsupported,
    /// <summary>
    /// Indicates that the VR view render implementation path uses sequential views.
    /// </summary>
    SequentialViews,
    /// <summary>
    /// Indicates that the VR view render implementation path uses parallel command buffer recording.
    /// </summary>
    ParallelCommandBufferRecording,
    /// <summary>
    /// Indicates that the VR view render implementation path uses true single-pass stereo.
    /// </summary>
    TrueSinglePassStereo,
}
