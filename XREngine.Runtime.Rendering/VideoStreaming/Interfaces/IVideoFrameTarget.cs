namespace XREngine.Rendering.VideoStreaming.Interfaces;

/// <summary>
/// A surface that shows decoded video frames that another owner supplies, for example a cutscene video track.
/// The surface uploads the frame through its own GPU upload path on the render thread.
/// </summary>
public interface IVideoFrameTarget
{
    /// <summary>
    /// Gives the surface the frame to show. The surface copies the pixel data before the call returns, so the caller can
    /// use its buffer again. A later frame replaces a frame that the render thread did not upload yet.
    /// </summary>
    /// <param name="frame">The frame. Only <see cref="VideoPixelFormat.Rgb24"/> frames are shown.</param>
    void SubmitFrame(in DecodedVideoFrame frame);

    /// <summary>Stops showing supplied frames. The surface goes back to its own content.</summary>
    void ClearFrame();
}
