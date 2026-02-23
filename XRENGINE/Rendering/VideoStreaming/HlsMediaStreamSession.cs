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

    /// <summary>
    /// Maximum lookahead tolerance for audio-clock-based video pacing.
    /// A video frame is considered "due" when its PTS is within this many
    /// ticks ahead of the current audio clock. Matched to the consumer's
    /// VideoHoldThresholdTicks (+40 ms) so both layers agree on what "too
    /// far ahead" means and the consumer hold check acts as a true safety net.
    /// </summary>
    private static readonly long TargetVideoBufferTicks = TimeSpan.TicksPerMillisecond * 40;

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
    private int _videoQueueCapacity = 180;

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

    /// <inheritdoc />
    public int QueuedVideoFrameCount { get { lock (_sync) return _videoFrames.Count; } }

    /// <inheritdoc />
    public int QueuedAudioFrameCount { get { lock (_sync) return _audioFrames.Count; } }

    /// <inheritdoc />
    public long EstimatedQueuedVideoDurationTicks
    {
        get
        {
            lock (_sync)
            {
                if (_videoFrames.Count <= 0)
                    return 0;

                long frameDurationTicks = Math.Clamp(_fallbackFrameDurationTicks, MinFrameDurationTicks, MaxSaneFrameDurationTicks);
                return _videoFrames.Count * frameDurationTicks;
            }
        }
    }

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
    /// Attempts to dequeue the next video frame, using the audio clock as the
    /// primary pacing reference so video never drifts ahead of or behind audio.
    /// Falls back to wall-clock pacing only when no audio clock is available
    /// (e.g. audio-free streams or before audio starts).
    /// </summary>
    /// <param name="audioClockTicks">
    /// Current audio clock position in stream-PTS ticks. When &gt; 0, video
    /// frames are released when their PTS &le; audioClockTicks, keeping video
    /// perfectly synced to the audio timeline. When 0, wall-clock fallback is used.
    /// </param>
    /// <param name="frame">The dequeued frame, if available.</param>
    /// <returns><c>true</c> if a frame was dequeued; <c>false</c> if it's not yet time or the queue is empty.</returns>
    public bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            if (_videoFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            // Peek at the next candidate without removing it yet.
            DecodedVideoFrame candidate = _videoFrames.Peek();
            long candidatePts = candidate.PresentationTimestampTicks;
            bool hasValidPts = candidatePts > 0 && _latestQueuedVideoPts >= candidatePts;

            // ── Audio-clock-based pacing (primary path) ──
            // When the audio clock is active, video is released purely based
            // on the audio timeline. A frame is "due" when its PTS is at or
            // before the audio clock + a small look-ahead tolerance. This
            // ensures video never races ahead of audio and never independently
            // speeds up to meet wall-clock realtime.
            //
            // Only ONE frame is dequeued per call. The render loop runs at
            // 60+ fps while video is typically 30 fps, so there are ~2 render
            // calls per video frame — plenty of headroom to keep up. During
            // minor stalls, video naturally catches up at one frame per render
            // pass (smooth and imperceptible). A multi-frame sweep was tried
            // but it amplified clock estimation noise into visible speed-up
            // bursts.
            if (audioClockTicks > 0 && hasValidPts)
            {
                if (candidatePts > audioClockTicks + TargetVideoBufferTicks)
                {
                    // Frame is in the future relative to audio — not due yet.
                    frame = default;
                    return false;
                }

                // Frame is due — dequeue it.
                frame = _videoFrames.Dequeue();

                // Update frame-duration tracking.
                long dequeuedPts = frame.PresentationTimestampTicks;
                if (dequeuedPts > 0)
                {
                    if (_lastDequeuedVideoPts > 0)
                    {
                        long deltaTicks = dequeuedPts - _lastDequeuedVideoPts;
                        if (deltaTicks >= MinFrameDurationTicks && deltaTicks <= MaxSaneFrameDurationTicks)
                            _fallbackFrameDurationTicks = deltaTicks;
                    }
                    _lastDequeuedVideoPts = dequeuedPts;
                }

                _lastVideoDequeueWallTicks = GetNowTicks();
                return true;
            }

            // ── Wall-clock fallback (no audio clock yet, or no valid PTS) ──
            // Used during startup before audio begins, or for audio-free streams.
            if (!_videoPlaybackStarted)
            {
                _videoPlaybackStarted = true;
                _videoPlaybackBaseWallTicks = GetNowTicks();
            }

            long nowTicks = GetNowTicks();

            // Stall recovery: clamp the last-dequeue timestamp so a render-loop
            // pause doesn't cause a burst of instantly-due frames.
            if (_lastVideoDequeueWallTicks > 0)
            {
                long stallThreshold = _fallbackFrameDurationTicks * 3;
                if (nowTicks - _lastVideoDequeueWallTicks > stallThreshold)
                    _lastVideoDequeueWallTicks = nowTicks - _fallbackFrameDurationTicks;
            }

            long expectedFrameTicks = _fallbackFrameDurationTicks;
            if (hasValidPts && _lastDequeuedVideoPts > 0)
            {
                long deltaTicks = candidatePts - _lastDequeuedVideoPts;
                if (deltaTicks >= MinFrameDurationTicks && deltaTicks <= MaxSaneFrameDurationTicks)
                    expectedFrameTicks = deltaTicks;
            }

            long dueTicks = _lastVideoDequeueWallTicks > 0
                ? _lastVideoDequeueWallTicks + expectedFrameTicks
                : _videoPlaybackBaseWallTicks;

            if (hasValidPts && dueTicks - nowTicks > MaxFutureDueWaitTicks)
                dueTicks = nowTicks + Math.Min(expectedFrameTicks, MaxFutureDueWaitTicks);

            if (nowTicks < dueTicks)
            {
                frame = default;
                return false;
            }

            // If the render loop has fallen far behind while running in
            // wall-clock mode (no audio clock), drop stale queued frames so
            // playback stays near real time instead of devolving into
            // slow-motion at one frame per render pass.
            if (_videoFrames.Count > 1 && expectedFrameTicks > 0)
            {
                long lagTicks = nowTicks - dueTicks;
                if (lagTicks > expectedFrameTicks)
                {
                    int framesBehind = (int)(lagTicks / expectedFrameTicks);
                    int framesToDrop = Math.Min(_videoFrames.Count - 1, Math.Max(0, framesBehind - 1));

                    while (framesToDrop-- > 0 && _videoFrames.Count > 1)
                    {
                        DecodedVideoFrame dropped = _videoFrames.Dequeue();
                        long droppedPts = dropped.PresentationTimestampTicks;
                        if (droppedPts > 0)
                            _lastDequeuedVideoPts = droppedPts;

                        _lastVideoDequeueWallTicks = _lastVideoDequeueWallTicks > 0
                            ? _lastVideoDequeueWallTicks + expectedFrameTicks
                            : nowTicks - expectedFrameTicks;
                    }

                    // Re-evaluate due-time after catch-up drops.
                    if (_videoFrames.Count > 0)
                    {
                        DecodedVideoFrame next = _videoFrames.Peek();
                        long nextPts = next.PresentationTimestampTicks;
                        if (nextPts > 0 && _lastDequeuedVideoPts > 0)
                        {
                            long dt = nextPts - _lastDequeuedVideoPts;
                            if (dt >= MinFrameDurationTicks && dt <= MaxSaneFrameDurationTicks)
                                expectedFrameTicks = dt;
                        }

                        dueTicks = _lastVideoDequeueWallTicks > 0
                            ? _lastVideoDequeueWallTicks + expectedFrameTicks
                            : nowTicks;

                        if (nowTicks < dueTicks)
                        {
                            frame = default;
                            return false;
                        }
                    }
                }
            }

            // Frame is due via wall-clock fallback.
            frame = _videoFrames.Dequeue();

            long pts = frame.PresentationTimestampTicks;
            if (pts > 0)
            {
                if (_lastDequeuedVideoPts > 0)
                {
                    long dt = pts - _lastDequeuedVideoPts;
                    if (dt >= MinFrameDurationTicks && dt <= MaxSaneFrameDurationTicks)
                        _fallbackFrameDurationTicks = dt;
                }
                _lastDequeuedVideoPts = pts;
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
    /// a video frame has been decoded. Non-blocking: if the queue is full the
    /// incoming frame is dropped immediately so the decode thread can move on
    /// to audio packets without stalling. The previous bounded-wait approach
    /// (16 × Sleep(1)) burned nearly all of the decode thread's time budget
    /// when the render loop was slow or the window was unfocused, starving
    /// audio decode entirely.
    /// </summary>
    private void OnDecodedVideoFrame(DecodedVideoFrame frame)
    {
        // Short bounded wait (2 iterations × Sleep(1) ≈ 2–30 ms).
        // With the large default queue (180 frames ≈ 3 s at 60 fps) this
        // rarely triggers, but it gives the render thread a brief chance
        // to drain a slot before we drop.  Kept very short to avoid
        // starving audio decode which runs on the same thread.
        const int MaxWaitIterations = 2;

        for (int waited = 0; ; waited++)
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

            if (waited >= MaxWaitIterations)
                return; // Queue still full — drop this frame.

            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Called by <see cref="FFmpegStreamDecoder"/> on its decode thread when
    /// an audio frame has been decoded. Uses bounded backpressure with a short
    /// timeout so the decode thread is never blocked indefinitely — a stalled
    /// audio consumer cannot freeze video decode.
    /// </summary>
    private void OnDecodedAudioFrame(DecodedAudioFrame frame)
    {
        const int MaxWaitMs = 50;

        for (int waited = 0; ; waited++)
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

            if (waited >= MaxWaitMs)
                return; // Audio consumer is too slow — drop to unblock decode thread.

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
