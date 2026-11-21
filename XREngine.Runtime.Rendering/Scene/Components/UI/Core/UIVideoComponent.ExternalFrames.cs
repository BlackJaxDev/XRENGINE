using System;
using System.Threading;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.UI
{
    // ═══════════════════════════════════════════════════════════════════
    // UIVideoComponent — Frames From Another Owner
    //
    // A cutscene video track gives the component the frame to show. The
    // component copies it, and the render pass uploads it through the same
    // IVideoFrameGpuActions path as a streamed frame.
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent : IVideoFrameTarget
    {
        private readonly Lock _externalFrameLock = new();

        // The caller writes the back buffer. The render pass swaps it to the front and uploads the front outside the lock,
        // so a submit never waits for an upload.
        private byte[] _externalBackBuffer = [];
        private byte[] _externalFrontBuffer = [];
        private DecodedVideoFrame _externalBackFrame;
        private volatile bool _externalFramesActive;
        private bool _externalFramePending;
        private bool _externalGpuActionsMissingReported;

        /// <summary>True while another owner, for example a cutscene video track, supplies the frames. The stream does not show then.</summary>
        public bool IsShowingExternalFrames => _externalFramesActive;

        /// <inheritdoc/>
        public void SubmitFrame(in DecodedVideoFrame frame)
        {
            if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty || frame.Width <= 0 || frame.Height <= 0)
                return;

            lock (_externalFrameLock)
            {
                int length = frame.PackedData.Length;
                if (_externalBackBuffer.Length < length)
                    _externalBackBuffer = new byte[length];
                frame.PackedData.Span.CopyTo(_externalBackBuffer);
                _externalBackFrame = new DecodedVideoFrame(
                    frame.Width,
                    frame.Height,
                    frame.PresentationTimestampTicks,
                    frame.DurationTicks,
                    VideoPixelFormat.Rgb24,
                    new ReadOnlyMemory<byte>(_externalBackBuffer, 0, length));
                _externalFramePending = true;
                _externalFramesActive = true;
            }
        }

        /// <inheritdoc/>
        public void ClearFrame()
        {
            lock (_externalFrameLock)
            {
                _externalFramesActive = false;
                _externalFramePending = false;
            }
        }

        /// <summary>
        /// Uploads the newest supplied frame on the render thread. Returns true while supplied frames are active, so the
        /// caller does not drain its own stream.
        /// </summary>
        private bool UploadExternalFrame()
        {
            if (!_externalFramesActive)
                return false;

            DecodedVideoFrame frame;
            lock (_externalFrameLock)
            {
                if (!_externalFramesActive)
                    return false;
                if (!_externalFramePending)
                    return true;

                _externalFramePending = false;
                (_externalBackBuffer, _externalFrontBuffer) = (_externalFrontBuffer, _externalBackBuffer);
                frame = new DecodedVideoFrame(
                    _externalBackFrame.Width,
                    _externalBackFrame.Height,
                    _externalBackFrame.PresentationTimestampTicks,
                    _externalBackFrame.DurationTicks,
                    VideoPixelFormat.Rgb24,
                    new ReadOnlyMemory<byte>(_externalFrontBuffer, 0, _externalBackFrame.PackedData.Length));
            }

            if (_gpuVideoActions is null && AbstractRenderer.Current is { } renderer)
                _gpuVideoActions = RuntimeVideoStreamingServices.Current?.CreateVideoFrameGpuActions(renderer);
            if (_gpuVideoActions is null)
            {
                if (!_externalGpuActionsMissingReported)
                {
                    _externalGpuActionsMissingReported = true;
                    Debug.UIWarning("UIVideoComponent cannot show supplied video frames: no video GPU actions service is registered for the current renderer.");
                }
                return true;
            }

            ApplyDecodedVideoFrame(frame);
            return true;
        }
    }
}
