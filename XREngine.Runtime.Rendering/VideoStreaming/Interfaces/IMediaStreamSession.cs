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
    /// True if a seek can move the stream and complete: a session opened with
    /// <see cref="StreamOpenOptions.ClockDrivenPresentation"/> on a local file with a known duration and a seekable video
    /// stream. A session in the normal streaming mode, a live stream, a network stream, and an HLS playlist give false.
    /// </summary>
    bool CanSeek { get; }

    /// <summary>The message of the last failed seek, or null.</summary>
    string? LastSeekError { get; }

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

    /// <summary>
    /// Moves the video stream to a time, as <see cref="SeekAsync"/> does, with no task and no allocation. Poll the result
    /// with <see cref="GetSeekState"/>. A frame path, for example a drag of a cutscene playhead, uses this form.
    /// One caller at a time may seek a session.
    /// </summary>
    /// <param name="targetTicks">The stream time in 100 ns ticks, in the time line of the frame timestamps.</param>
    /// <param name="mode">The accuracy of the seek.</param>
    /// <returns>The id of the request, or -1 when <see cref="CanSeek"/> is false.</returns>
    int RequestSeek(long targetTicks, EVideoSeekMode mode);

    /// <summary>The state of a request of <see cref="RequestSeek"/>. A request that a newer request replaced is cancelled.</summary>
    /// <param name="requestId">The id that <see cref="RequestSeek"/> gave.</param>
    EVideoSeekState GetSeekState(int requestId);

    void Close();
}
