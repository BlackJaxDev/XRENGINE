namespace XREngine;

public enum EVrViewRenderMode
{
    /// <summary>
    /// Render the two eye views one after the other.
    /// </summary>
    SequentialViews,

    /// <summary>
    /// Request a stereo render path. OpenXR Vulkan uses true stereo when the
    /// layered staging path is available, and otherwise reports a compatibility
    /// path over per-eye swapchains.
    /// </summary>
    SinglePassStereo,

    /// <summary>
    /// Render the same per-eye output as sequential views while preparing safe
    /// command-buffer work concurrently where the backend supports it.
    /// </summary>
    ParallelCommandBufferRecording,
}
