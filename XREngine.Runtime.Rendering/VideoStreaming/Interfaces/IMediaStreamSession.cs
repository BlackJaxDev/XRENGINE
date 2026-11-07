using System;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IMediaStreamSession : IDisposable
{
    event Action<int, int>? VideoSizeChanged;

    bool IsOpen { get; }
    int QueuedVideoFrameCount { get; }
    int QueuedAudioFrameCount { get; }
    long EstimatedQueuedVideoDurationTicks { get; }

    /// <summary>
    /// True if <see cref="SeekAsync"/> can move the stream: an open local file with a known duration and a seekable video
    /// stream. A live stream, a network stream, and an HLS playlist give false.
    /// </summary>
    bool CanSeek { get; }

    Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken);
    void SetTargetFramebuffer(uint framebufferId);
    void Present();
    bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame);
    bool TryDequeueAudioFrame(out DecodedAudioFrame frame);

    /// <summary>
    /// Moves the video stream to a time. The call returns at once; the decoder thread does the work. The session drops the
    /// queued frames, so the next frame that <see cref="TryDequeueVideoFrame"/> gives comes from the new position.
    /// A newer seek supersedes an older one: the older task ends as cancelled and its frames are dropped.
    /// </summary>
    /// <param name="targetTicks">The stream time in 100 ns ticks, in the time line of the frame timestamps.</param>
    /// <param name="mode">The accuracy of the seek.</param>
    /// <param name="cancellationToken">Cancels the seek.</param>
    /// <returns>
    /// A task that completes when the first frame of the new position is in the queue. It ends as cancelled when a newer
    /// seek or the token stops it. It faults with <see cref="NotSupportedException"/> when <see cref="CanSeek"/> is false.
    /// </returns>
    Task SeekAsync(long targetTicks, EVideoSeekMode mode, CancellationToken cancellationToken);

    void Close();
}
