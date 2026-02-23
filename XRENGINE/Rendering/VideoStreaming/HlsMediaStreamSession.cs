using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Manages an HLS media stream session backed by <see cref="FFmpegStreamDecoder"/>.
/// <para>
/// Decoded video and audio frames are queued internally and served to the
/// consumer (typically the render / drain loop in <c>UIVideoComponent</c>)
/// via <see cref="TryDequeueVideoFrame"/> and <see cref="TryDequeueAudioFrame"/>.
/// </para>
/// <para>
/// The session applies wall-clock-based pacing on video dequeue so frames are
/// released at approximately the correct frame rate, and uses backpressure on
/// the decode thread to prevent the decoder from running ahead of the consumer.
/// </para>
/// </summary>
internal sealed class HlsMediaStreamSession : IMediaStreamSession
{
    // ═══════════════════════════════════════════════════════════════
    // Constants — Frame Timing
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Default frame duration assuming 30 fps (≈33.3 ms in ticks).</summary>
    private const long DefaultVideoFrameDurationTicks = TimeSpan.TicksPerSecond / 30;

    /// <summary>Minimum credible inter-frame interval (5 ms). Prevents division-by-zero-class timing.</summary>
    private const long MinFrameDurationTicks = TimeSpan.TicksPerMillisecond * 5;

    /// <summary>Maximum credible inter-frame interval (100 ms ≈ 10 fps). Anything larger is treated as a PTS gap.</summary>
    private const long MaxSaneFrameDurationTicks = TimeSpan.TicksPerMillisecond * 100;

    /// <summary>Cap on how far into the future a frame's due-time may be before we clamp it.</summary>
    private const long MaxFutureDueWaitTicks = TimeSpan.TicksPerMillisecond * 100;

    /// <summary>Target buffering depth used by the consumer to decide when to start playback.</summary>
    private static readonly long TargetVideoBufferTicks = TimeSpan.FromMilliseconds(500).Ticks;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Core Dependencies
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The underlying FFmpeg-based decoder that produces raw frames.</summary>
    private readonly FFmpegStreamDecoder _decoder;

    /// <summary>Lock guarding all mutable frame-queue and playback state.</summary>
    private readonly object _sync = new();

    // ═══════════════════════════════════════════════════════════════
    // Fields — Frame Queues
    // ═══════════════════════════════════════════════════════════════

    /// <summary>FIFO of decoded video frames awaiting consumption by the render loop.</summary>
    private readonly Queue<DecodedVideoFrame> _videoFrames = [];

    /// <summary>FIFO of decoded audio frames awaiting consumption by the audio subsystem.</summary>
    private readonly Queue<DecodedAudioFrame> _audioFrames = [];

    /// <summary>Maximum number of video frames buffered before backpressure blocks the decoder.</summary>
    private int _videoQueueCapacity = 24;

    /// <summary>Maximum number of audio frames buffered before backpressure blocks the decoder.</summary>
    private int _audioQueueCapacity = 8;

    /// <summary>Highest PTS enqueued so far — used to validate incoming PTS values.</summary>
    private long _latestQueuedVideoPts = long.MinValue;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Playback Pacing State
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Whether the first video frame has been dequeued (marks playback start).</summary>
    private bool _videoPlaybackStarted;

    /// <summary>Wall-clock tick at which video playback was first started.</summary>
    private long _videoPlaybackBaseWallTicks;

    /// <summary>
    /// Current estimate of the inter-frame duration in ticks.
    /// Starts at <see cref="DefaultVideoFrameDurationTicks"/> and is refined
    /// as actual PTS deltas are observed.
    /// </summary>
    private long _fallbackFrameDurationTicks = DefaultVideoFrameDurationTicks;

    /// <summary>PTS of the most recently dequeued video frame.</summary>
    private long _lastDequeuedVideoPts = long.MinValue;

    /// <summary>Wall-clock tick at which the last video frame was dequeued.</summary>
    private long _lastVideoDequeueWallTicks;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Session Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Set to unblock backpressure spin-waits during Close/Dispose.</summary>
    private volatile bool _closing;

    /// <summary>Indicates whether the session has been opened and not yet closed.</summary>
    private bool _isOpen;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    public HlsMediaStreamSession()
    {
        _decoder = new FFmpegStreamDecoder
        {
            OnVideoFrame = OnDecodedVideoFrame,
            OnAudioFrame = OnDecodedAudioFrame,
            OnVideoSizeChanged = (w, h) => VideoSizeChanged?.Invoke(w, h),
            OnVideoFrameRateDetected = OnVideoFrameRateDetected,
            OnError = err => Debug.UIWarning($"Stream decoder error: {err}")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Events & Properties
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public event Action<int, int>? VideoSizeChanged;

    /// <inheritdoc />
    public bool IsOpen => _isOpen;

    // ═══════════════════════════════════════════════════════════════
    // Public API — Session Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the HLS stream at <paramref name="url"/> and begins decoding
    /// on a background thread.
    /// </summary>
    /// <param name="url">HLS playlist URL (or any FFmpeg-supported stream URL).</param>
    /// <param name="options">Optional tuning parameters (queue sizes, HTTP headers, etc.).</param>
    /// <param name="cancellationToken">Token to cancel the open operation.</param>
    public async Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Apply caller-specified queue capacities (minimum 1).
        if (options is not null)
        {
            _videoQueueCapacity = Math.Max(1, options.VideoQueueCapacity);
            _audioQueueCapacity = Math.Max(1, options.AudioQueueCapacity);
        }

        await _decoder.OpenAsync(url, options, cancellationToken).ConfigureAwait(false);
        _isOpen = true;
    }

    /// <summary>
    /// No-op: FFmpeg decoder delivers CPU-side frames; GPU upload is handled
    /// by <c>IVideoFrameGpuActions</c> after dequeue.
    /// </summary>
    public void SetTargetFramebuffer(uint framebufferId) { }

    /// <summary>
    /// No-op: the render loop drains frames via <see cref="TryDequeueVideoFrame"/>
    /// and uploads them in <c>DrainStreamingFramesOnMainThread</c>.
    /// </summary>
    public void Present() { }

    /// <summary>
    /// Stops the decoder, drains all queued frames, and resets playback state.
    /// </summary>
    public void Close()
    {
        // Signal closing early to unblock any backpressure waits in decode callbacks.
        _closing = true;
        _decoder.Stop();

        lock (_sync)
        {
            _isOpen = false;
            _videoFrames.Clear();
            _audioFrames.Clear();
            _latestQueuedVideoPts = long.MinValue;
            _videoPlaybackStarted = false;
            _videoPlaybackBaseWallTicks = 0;
            _fallbackFrameDurationTicks = DefaultVideoFrameDurationTicks;
            _lastDequeuedVideoPts = long.MinValue;
            _lastVideoDequeueWallTicks = 0;
        }
    }

    /// <summary>
    /// Permanently disposes the session and its underlying decoder resources.
    /// </summary>
    public void Dispose()
    {
        _closing = true;
        _decoder.Dispose();
        _isOpen = false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public API — Frame Dequeue (called by the render/drain loop)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to dequeue the next video frame, applying wall-clock pacing
    /// so frames are released at approximately the correct frame rate.
    /// </summary>
    /// <param name="audioClockTicks">
    /// Current audio clock position in ticks (currently unused; pacing is
    /// wall-clock based, but the parameter is preserved for future A/V sync).
    /// </param>
    /// <param name="frame">The dequeued frame, if available.</param>
    /// <returns><c>true</c> if a frame was dequeued; <c>false</c> if it's not yet time or the queue is empty.</returns>
    public bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            _ = audioClockTicks; // Reserved for future A/V sync use.

            if (_videoFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            // Peek at the next candidate without removing it yet.
            DecodedVideoFrame candidate = _videoFrames.Peek();
            long candidatePts = candidate.PresentationTimestampTicks;
            bool hasValidPts = candidatePts > 0 && _latestQueuedVideoPts >= candidatePts;

            // Record the wall-clock start of playback on the first dequeue attempt.
            if (!_videoPlaybackStarted)
            {
                _videoPlaybackStarted = true;
                _videoPlaybackBaseWallTicks = GetNowTicks();
            }

            long nowTicks = GetNowTicks();

            // ── Compute the wall-clock time at which this frame is "due" ──
            if (hasValidPts)
            {
                // Use PTS delta to estimate the expected inter-frame duration.
                long expectedFrameTicks = _fallbackFrameDurationTicks;
                if (_lastDequeuedVideoPts > 0)
                {
                    long deltaTicks = candidatePts - _lastDequeuedVideoPts;
                    if (deltaTicks >= MinFrameDurationTicks && deltaTicks <= MaxSaneFrameDurationTicks)
                        expectedFrameTicks = deltaTicks;
                }

                long dueTicks = _lastVideoDequeueWallTicks > 0
                    ? _lastVideoDequeueWallTicks + expectedFrameTicks
                    : _videoPlaybackBaseWallTicks;

                // Clamp excessively far-future due times to avoid long stalls
                // (e.g. after a PTS discontinuity).
                if (dueTicks - nowTicks > MaxFutureDueWaitTicks)
                    dueTicks = nowTicks + Math.Min(expectedFrameTicks, MaxFutureDueWaitTicks);

                // Not yet time — leave the frame in the queue.
                if (nowTicks < dueTicks)
                {
                    frame = default;
                    return false;
                }
            }
            else
            {
                // No valid PTS — fall back to the fixed frame-rate cadence.
                long dueTicks = _lastVideoDequeueWallTicks > 0
                    ? _lastVideoDequeueWallTicks + _fallbackFrameDurationTicks
                    : _videoPlaybackBaseWallTicks;

                if (nowTicks < dueTicks)
                {
                    frame = default;
                    return false;
                }
            }

            // ── Frame is due — dequeue and update tracking state ──
            frame = _videoFrames.Dequeue();

            long dequeuedPts = frame.PresentationTimestampTicks;
            if (dequeuedPts > 0)
            {
                // Refine the fallback duration estimate from consecutive PTS deltas.
                if (_lastDequeuedVideoPts > 0)
                {
                    long deltaTicks = dequeuedPts - _lastDequeuedVideoPts;
                    if (deltaTicks >= MinFrameDurationTicks && deltaTicks <= MaxSaneFrameDurationTicks)
                        _fallbackFrameDurationTicks = deltaTicks;
                }

                _lastDequeuedVideoPts = dequeuedPts;
            }

            _lastVideoDequeueWallTicks = nowTicks;
            return true;
        }
    }

    /// <summary>
    /// Attempts to dequeue the next decoded audio frame.
    /// Audio frames are not paced here — the consumer submits them to
    /// OpenAL and relies on the audio hardware for playback timing.
    /// </summary>
    /// <param name="frame">The dequeued audio frame, if available.</param>
    /// <returns><c>true</c> if a frame was dequeued; <c>false</c> if the queue is empty.</returns>
    public bool TryDequeueAudioFrame(out DecodedAudioFrame frame)
    {
        lock (_sync)
        {
            if (_audioFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _audioFrames.Dequeue();
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Decoder Callbacks — Backpressure Enqueue
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by <see cref="FFmpegStreamDecoder"/> on its decode thread when
    /// a video frame has been decoded. Blocks (backpressure) until the queue
    /// has space, preventing the decoder from outrunning the consumer and
    /// silently dropping frames (which caused visible video skipping).
    /// </summary>
    private void OnDecodedVideoFrame(DecodedVideoFrame frame)
    {
        while (true)
        {
            if (_closing)
                return;

            lock (_sync)
            {
                if (!_isOpen)
                    return;

                if (_videoFrames.Count < _videoQueueCapacity)
                {
                    _videoFrames.Enqueue(frame);
                    if (frame.PresentationTimestampTicks > 0)
                        _latestQueuedVideoPts = frame.PresentationTimestampTicks;
                    return;
                }
            }

            // Queue is full — yield briefly and retry.
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Called by <see cref="FFmpegStreamDecoder"/> on its decode thread when
    /// an audio frame has been decoded. Blocks (backpressure) until the queue
    /// has space.
    /// </summary>
    private void OnDecodedAudioFrame(DecodedAudioFrame frame)
    {
        while (true)
        {
            if (_closing)
                return;

            lock (_sync)
            {
                if (!_isOpen)
                    return;

                if (_audioFrames.Count < _audioQueueCapacity)
                {
                    _audioFrames.Enqueue(frame);
                    return;
                }
            }

            // Queue is full — yield briefly and retry.
            Thread.Sleep(1);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Decoder Callbacks — Frame Rate Detection
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Called when the decoder detects the stream's frame rate.
    /// Converts fps to a tick-based duration and stores it as the fallback
    /// pacing interval.
    /// </summary>
    private void OnVideoFrameRateDetected(double fps)
    {
        if (fps <= 0)
            return;

        long frameTicks = (long)Math.Round(TimeSpan.TicksPerSecond / fps);
        frameTicks = Math.Clamp(frameTicks, MinFrameDurationTicks, MaxSaneFrameDurationTicks);

        lock (_sync)
        {
            _fallbackFrameDurationTicks = frameTicks;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the current engine elapsed time converted to 100-nanosecond ticks.
    /// </summary>
    private static long GetNowTicks()
        => (long)(Engine.ElapsedTime * TimeSpan.TicksPerSecond);
}
