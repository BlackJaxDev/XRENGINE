using XREngine.Imaging;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Receives the pixels of each captured viewport frame instead of a PNG file on disk.
/// <see cref="ViewportSequenceCaptureSession"/> calls the sink on a worker thread after the GPU readback completes.
/// Frames can arrive out of capture order, so the sink must order them itself when order matters.
/// </summary>
internal interface IViewportSequenceFrameSink
{
    /// <summary>
    /// Copies or consumes the pixels of one captured frame. The session disposes <paramref name="image"/> after
    /// this call returns, so the sink must copy any pixel data that it keeps. An exception marks the frame as failed
    /// and stops the capture session.
    /// </summary>
    /// <param name="captureIndex">The zero-based capture index of the frame in its session.</param>
    /// <param name="image">The read-back frame image at the session output size.</param>
    void WriteFrame(int captureIndex, RuntimeImage image);
}
