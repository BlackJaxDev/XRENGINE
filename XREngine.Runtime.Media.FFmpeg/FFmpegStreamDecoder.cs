using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Opens an HLS (or other FFmpeg-supported) URL and decodes video/audio frames
/// on a background thread, delivering them via callbacks.
/// <para>
/// Architecture: A single background thread runs <see cref="DecodeLoop"/>, which
/// reads packets from FFmpeg and decodes them into raw video (RGB24) and audio
/// (interleaved PCM) frames. Decoded frames are delivered to <see cref="OnVideoFrame"/>
/// and <see cref="OnAudioFrame"/> callbacks, which are typically wired to
/// <see cref="HlsMediaStreamSession"/>'s backpressure-enqueue methods.
/// </para>
/// <para>
/// Decoder-side frame pacing is intentionally disabled. Playback timing is
/// controlled by backpressure from the session queue: the decode thread blocks
/// in the callback when the queue is full.
/// </para>
/// </summary>
internal sealed class FFmpegStreamDecoder : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    /// <summary>All decoded video frames are converted to this pixel format before delivery.</summary>
    private const AVPixelFormat TargetPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

    /// <summary>Maximum consecutive packet-read failures before the decode loop aborts.</summary>
    private const int MaxReadRetries = 12;

    /// <summary>The number of demuxer seeks that one seek request can make before it fails.</summary>
    private const int MaxSeekAttempts = 4;

    /// <summary>The time in milliseconds that the clock-driven loop waits for a seek or a stop while it has no work.</summary>
    private const int IdleWaitMilliseconds = 50;

    // Results of ReceiveVideoFrame.
    private const int ReceiveFrame = 0;
    private const int ReceiveEndOfStream = 1;
    private const int ReceiveError = 2;
    private const int ReceiveInterrupted = 3;

    private static readonly AVRational TicksTimeBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };

    // ═══════════════════════════════════════════════════════════════
    // Fields — Thread Safety & Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Lock guarding init/cleanup of FFmpeg contexts (decode thread uses them lock-free once started).</summary>
    private readonly Lock _lock = new();

    /// <summary>Set once <see cref="Dispose"/> is called to prevent double-cleanup.</summary>
    private volatile bool _disposed;

    /// <summary>Controls the decode loop — cleared to request a graceful stop.</summary>
    private volatile bool _running;

    /// <summary>The background decode thread (started by <see cref="OpenAsync"/>).</summary>
    private Thread? _decodeThread;

    /// <summary>
    /// Set true when Stop/Dispose has been requested. OpenAsync checks this
    /// before starting decode after native open to avoid start-after-stop races.
    /// </summary>
    private volatile bool _stopRequested;

    // ═══════════════════════════════════════════════════════════════
    // Fields — FFmpeg Native Contexts
    // All access guarded by _lock during init/cleanup, then only
    // touched on the decode thread.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Top-level FFmpeg demuxer context (owns the opened stream).</summary>
    private unsafe AVFormatContext* _formatContext;

    /// <summary>Video decoder context (H.264, etc.).</summary>
    private unsafe AVCodecContext* _videoCodecContext;

    /// <summary>Audio decoder context (AAC, etc.).</summary>
    private unsafe AVCodecContext* _audioCodecContext;

    /// <summary>Hardware device context (unused — HW accel is currently disabled).</summary>
    private unsafe AVBufferRef* _hwDeviceContext;

    /// <summary>Reusable frame buffer for the current decoded frame.</summary>
    private unsafe AVFrame* _frame;

    /// <summary>Reusable packet buffer for the current demuxed packet.</summary>
    private unsafe AVPacket* _packet;

    /// <summary>Software scaler context for pixel-format conversion to <see cref="TargetPixelFormat"/>.</summary>
    private unsafe SwsContext* _swsContext;

    /// <summary>Pixel format reported by hardware acceleration (used for HW→SW transfer detection).</summary>
    private AVPixelFormat _hwPixelFormat;

    // ── Cached SwsContext parameters (to detect when re-creation is needed) ──
    private int _swsWidth;
    private int _swsHeight;
    private AVPixelFormat _swsSrcFormat;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Stream Indices & State
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Index of the selected video stream within the format context, or -1 if none.</summary>
    private int _videoStreamIndex = -1;

    /// <summary>Index of the selected audio stream within the format context, or -1 if none.</summary>
    private int _audioStreamIndex = -1;

    /// <summary>Running count of consecutive read failures (reset on success).</summary>
    private int _readRetryCount;

    /// <summary>Last reported video dimensions (used to detect size changes).</summary>
    private int _lastReportedWidth;
    private int _lastReportedHeight;

    /// <summary>
    /// Estimated video frame duration in .NET ticks (100 ns). Seeded at 60 fps
    /// and refined from stream metadata when available.
    /// </summary>
    private long _videoFrameDurationTicks = TimeSpan.TicksPerSecond / 60;

    /// <summary>Last emitted video PTS in .NET ticks (monotonic timeline).</summary>
    private long _lastVideoPtsTicks = long.MinValue;

    /// <summary>
    /// Current synthesized video PTS used when FFmpeg provides missing or
    /// unusable timestamps.
    /// </summary>
    private long _syntheticVideoPtsTicks = long.MinValue;

    /// <summary>Consecutive count of video frames with missing/unusable source PTS.</summary>
    private int _videoMissingPtsStreak;

    // ═══════════════════════════════════════════════════════════════
    // Fields — Clock-Driven Mode And Seeking
    // ═══════════════════════════════════════════════════════════════

    /// <summary>True when the caller paces every frame. The loop then stays alive at the end of the file for a later seek.</summary>
    private bool _clockDriven;

    /// <summary>True to open and decode the audio stream.</summary>
    private bool _decodeAudio = true;

    /// <summary>True when the opened source is a local file that supports a seek. Set once at open.</summary>
    private volatile bool _isSeekable;

    /// <summary>The reusable RGB frame that receives the scaler output. It changes only when the frame size changes.</summary>
    private unsafe AVFrame* _rgbFrame;

    /// <summary>Holds the last decoded frame before the target during an exact seek, so the end of the file can still show it.</summary>
    private unsafe AVFrame* _heldFrame;

    /// <summary>True after the decoder got the flush packet at the end of the file. A seek clears it.</summary>
    private bool _drainSent;

    /// <summary>True after a keyframe seek or a cancelled seek. The clock-driven loop waits for the next seek.</summary>
    private bool _paused;

    /// <summary>True after the clock-driven loop reached the end of the file. A seek clears it.</summary>
    private bool _endOfStream;

    /// <summary>Wakes the clock-driven loop for a seek or a stop.</summary>
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>True after <see cref="Dispose"/> disposed <see cref="_wake"/>.</summary>
    private volatile bool _wakeDisposed;

    /// <summary>Guards the pending and the active seek request.</summary>
    private readonly Lock _seekLock = new();

    // The newest request that the loop has not started. The fields are reused, so a request allocates nothing.
    private long _pendingTarget;
    private EVideoSeekMode _pendingMode;
    private int _pendingGeneration;
    private TaskCompletionSource? _pendingCompletion;
    private volatile bool _hasPendingSeek;

    // The request that the loop runs now.
    private volatile bool _seekActive;
    private volatile bool _activeCancelRequested;
    private TaskCompletionSource? _activeCompletion;
    private volatile int _executingGeneration;

    // The id of the last request with each result.
    private volatile int _completedGeneration = -1;
    private volatile int _failedGeneration = -1;
    private volatile int _cancelledGeneration = -1;
    private string? _lastSeekError;

    /// <summary>
    /// The start time of the video stream in ticks, in the clock-driven mode. Frame times and seek targets use the stream
    /// start as zero, so a file with a non-zero start time plays from its first frame.
    /// </summary>
    private long _ptsOriginTicks;

    // ═══════════════════════════════════════════════════════════════
    // Callbacks — Wired by HlsMediaStreamSession
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Invoked on the decode thread for each decoded video frame.</summary>
    public Action<DecodedVideoFrame>? OnVideoFrame { get; set; }

    /// <summary>Invoked on the decode thread for each decoded audio frame.</summary>
    public Action<DecodedAudioFrame>? OnAudioFrame { get; set; }

    /// <summary>Invoked when the decoded video resolution changes (e.g. HLS quality switch).</summary>
    public Action<int, int>? OnVideoSizeChanged { get; set; }

    /// <summary>Invoked once when the video frame rate is detected from stream metadata.</summary>
    public Action<double>? OnVideoFrameRateDetected { get; set; }

    /// <summary>Invoked for non-fatal errors and warnings.</summary>
    public Action<string>? OnError { get; set; }

    /// <summary>
    /// Gives the managed buffer of a decoded video frame, with exactly the requested length. Null allocates a new array for
    /// each frame. The clock-driven session gives pooled buffers, so steady playback allocates no frame data.
    /// </summary>
    public Func<int, byte[]>? RentVideoBuffer { get; set; }

    /// <summary>
    /// True when a seek can complete: the decoder runs in the clock-driven mode, whose loop runs the seeks, on a local file
    /// with a known duration and a video stream.
    /// </summary>
    public bool IsSeekable => _isSeekable && _clockDriven;

    /// <summary>True while a seek waits for the decoder thread. A frame callback can use it to stop a wait for queue space.</summary>
    public bool HasPendingSeek => _hasPendingSeek;

    /// <summary>
    /// The generation of the last seek that the decoder thread started. Frames that the decoder delivers come from this
    /// position. It is zero before the first seek. Read it in a frame callback to drop frames of an old position.
    /// </summary>
    public int ExecutingGeneration => _executingGeneration;

    // ═══════════════════════════════════════════════════════════════
    // Public API — Open / Stop / Dispose
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the stream at <paramref name="url"/> with optional HTTP/HLS settings,
    /// then starts a background decode thread that reads and decodes packets.
    /// </summary>
    /// <param name="url">Stream URL (HLS playlist, RTMP, file, etc.).</param>
    /// <param name="options">Optional HTTP headers, reconnect policy, queue sizes.</param>
    /// <param name="cancellationToken">Token to cancel the open operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if FFmpeg fails to open the URL.</exception>
    public unsafe Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            lock (_lock)
            {
                _stopRequested = false;
                _clockDriven = options?.ClockDrivenPresentation == true;
                _decodeAudio = options?.DecodeAudio ?? true;
                _paused = false;
                _endOfStream = false;
                _drainSent = false;

                bool opened = false;
                try
                {
                    opened = AllocateAndOpen(url, options);
                }
                catch (Exception ex)
                {
                    // avformat_open_input (or other native calls) can throw a
                    // native access-violation / SEH exception rather than
                    // returning a negative error code. Ensure we always clean
                    // up native resources and surface a clear error.
                    CleanupUnsafe();
                    throw new InvalidOperationException(
                        $"FFmpeg native exception while opening '{url}': {ex.Message}", ex);
                }

                if (!opened)
                {
                    CleanupUnsafe();
                    LogWarning($"FFmpeg failed to open any streams for URL: {url}");
                    return;
                }

                // Stop/Dispose may have been requested while native open was in progress.
                if (_stopRequested || _disposed)
                {
                    CleanupUnsafe();
                    return;
                }

                _running = true;
                LogInfo($"FFmpeg stream decoder opened: url='{url}', video={_videoStreamIndex}, audio={_audioStreamIndex}");

                _decodeThread = new Thread(DecodeLoop)
                {
                    Name = "FFmpegStreamDecoder",
                    IsBackground = true
                };
                _decodeThread.Start();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Signals the decode loop to stop, waits up to 3 seconds for the thread
    /// to exit, and then frees all native FFmpeg resources.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;

        Thread? decodeThread;
        lock (_lock)
        {
            _running = false;
            decodeThread = _decodeThread;
        }

        lock (_seekLock)
        {
            if (!_wakeDisposed)
                _wake.Set();
        }
        decodeThread?.Join(TimeSpan.FromSeconds(3));
        CancelSeeks();

        lock (_lock)
        {
            _running = false;
            CleanupUnsafe();
            _decodeThread = null;
        }
    }

    /// <summary>
    /// Stops and disposes all resources. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        lock (_seekLock)
        {
            _wakeDisposed = true;
            _wake.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Seeking
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Asks the decoder thread to move the video stream. The call returns at once and allocates nothing. A newer request
    /// supersedes this one: a request that did not start never runs, and a running request stops at its next frame.
    /// Poll the result with <see cref="GetSeekState"/>.
    /// </summary>
    /// <param name="targetTicks">The target time in 100 ns ticks, in the time line of the frame timestamps.</param>
    /// <param name="mode">The accuracy of the seek.</param>
    /// <param name="generation">The id of the request. It must be larger than the id of every earlier request.</param>
    /// <param name="completion">A task source to complete with the request, or null.</param>
    /// <returns>False if the decoder does not run in the clock-driven mode on a seekable source.</returns>
    public bool RequestSeek(long targetTicks, EVideoSeekMode mode, int generation, TaskCompletionSource? completion)
    {
        TaskCompletionSource? superseded;
        lock (_seekLock)
        {
            // The running state is read under the lock, so a stop that cancels the requests cannot miss a late request.
            if (!_isSeekable || !_clockDriven || !_running || _disposed)
                return false;

            superseded = _pendingCompletion;
            _pendingTarget = targetTicks;
            _pendingMode = mode;
            _pendingGeneration = generation;
            _pendingCompletion = completion;
            _hasPendingSeek = true;

            // Dispose disposes the wake event under this lock, so the set cannot meet a disposed event.
            if (!_wakeDisposed)
                _wake.Set();
        }
        superseded?.TrySetCanceled();
        return true;
    }

    /// <summary>
    /// Asks the decoder thread to move the video stream, and gives a task for the result. The task allocates; a caller in a
    /// frame path uses <see cref="RequestSeek"/> and <see cref="GetSeekState"/>.
    /// </summary>
    public Task SeekAsync(long targetTicks, EVideoSeekMode mode, int generation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!RequestSeek(targetTicks, mode, generation, completion))
            return Task.FromException(new NotSupportedException("The stream is not open in the clock-driven mode, or it does not support a seek."));

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(() => CancelGeneration(generation));
            completion.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration, TaskScheduler.Default);
        }
        return completion.Task;
    }

    /// <summary>The state of the request with an id. A request that a newer request replaced is cancelled.</summary>
    /// <param name="generation">The id of the request.</param>
    public EVideoSeekState GetSeekState(int generation)
    {
        if (_completedGeneration == generation)
            return EVideoSeekState.Completed;
        if (_failedGeneration == generation)
            return EVideoSeekState.Failed;
        if (_cancelledGeneration == generation)
            return EVideoSeekState.Cancelled;
        lock (_seekLock)
        {
            int newest = _hasPendingSeek ? _pendingGeneration : _executingGeneration;
            if (generation < newest || (!_hasPendingSeek && !_seekActive && generation != _executingGeneration))
                return EVideoSeekState.Cancelled;
            return _running ? EVideoSeekState.Pending : EVideoSeekState.Cancelled;
        }
    }

    /// <summary>The message of the last failed seek, or null.</summary>
    public string? LastSeekError => Volatile.Read(ref _lastSeekError);

    private void CancelGeneration(int generation)
    {
        TaskCompletionSource? completion = null;
        lock (_seekLock)
        {
            if (_hasPendingSeek && _pendingGeneration == generation)
            {
                completion = _pendingCompletion;
                _pendingCompletion = null;
                _hasPendingSeek = false;
                _cancelledGeneration = generation;
            }
            else if (_seekActive && _executingGeneration == generation)
            {
                _activeCancelRequested = true;
            }
        }
        completion?.TrySetCanceled();
    }

    private bool TakePendingSeek(out long target, out EVideoSeekMode mode)
    {
        lock (_seekLock)
        {
            target = _pendingTarget;
            mode = _pendingMode;
            if (!_hasPendingSeek)
                return false;

            _hasPendingSeek = false;
            _seekActive = true;
            _activeCancelRequested = false;
            _executingGeneration = _pendingGeneration;
            _activeCompletion = _pendingCompletion;
            _pendingCompletion = null;
            return true;
        }
    }

    private void CompleteActiveSeek(EVideoSeekState state, string? error)
    {
        TaskCompletionSource? completion;
        lock (_seekLock)
        {
            // The result is set before the request stops being active, so a poll never sees a finished request as pending.
            int generation = _executingGeneration;
            if (state == EVideoSeekState.Completed)
                _completedGeneration = generation;
            else if (state == EVideoSeekState.Failed)
            {
                Volatile.Write(ref _lastSeekError, error);
                _failedGeneration = generation;
            }
            else
                _cancelledGeneration = generation;
            completion = _activeCompletion;
            _activeCompletion = null;
            _seekActive = false;
        }

        if (state == EVideoSeekState.Completed)
            completion?.TrySetResult();
        else if (state == EVideoSeekState.Failed)
            completion?.TrySetException(new InvalidOperationException(error));
        else
            completion?.TrySetCanceled();
    }

    // Ends every waiting and running seek as cancelled. The loop is not running after a stop.
    private void CancelSeeks()
    {
        TaskCompletionSource? pending;
        TaskCompletionSource? active;
        lock (_seekLock)
        {
            pending = _pendingCompletion;
            active = _activeCompletion;
            if (_hasPendingSeek)
                _cancelledGeneration = _pendingGeneration;
            _pendingCompletion = null;
            _activeCompletion = null;
            _hasPendingSeek = false;
            _seekActive = false;
        }
        pending?.TrySetCanceled();
        active?.TrySetCanceled();
    }

    private bool IsSuperseded()
        => _hasPendingSeek || _activeCancelRequested || !_running || _disposed;

    /// <summary>
    /// Runs one seek on the decoder thread. The demuxer seeks to the keyframe at or before the target. If the first frame is
    /// still after the target, the seek starts again further back, and the last attempt starts at the stream start.
    /// A keyframe seek delivers that frame and pauses the loop. An exact seek decodes forward until the next frame is after
    /// the target, delivers the frame before it (the frame that shows at the target), and lets the loop decode forward.
    /// </summary>
    private unsafe void ExecuteSeek(long targetTicks, EVideoSeekMode mode)
    {
        _paused = false;
        _endOfStream = false;
        AVStream* stream = _formatContext->streams[_videoStreamIndex];
        AVRational timeBase = stream->time_base;
        long startTs = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0L;
        long seekTicks = targetTicks;
        bool positioned = false;
        string? failure = null;
        for (int attempt = 0; attempt < MaxSeekAttempts && !positioned; attempt++)
        {
            if (IsSuperseded())
            {
                StopSuperseded();
                return;
            }

            bool fromStart = attempt == MaxSeekAttempts - 1;
            long seekTs = fromStart ? startTs : Math.Max(startTs, ffmpeg.av_rescale_q(seekTicks + _ptsOriginTicks, TicksTimeBase, timeBase));
            int result = ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (result < 0)
            {
                failure = $"av_seek_frame failed: {GetFFmpegError(result)}";
                seekTicks = targetTicks - BackoffTicks(attempt);
                continue;
            }

            ResetDecoderAfterSeek();
            int received = ReceiveVideoFrame(seeking: true);
            if (received == ReceiveInterrupted)
            {
                StopSuperseded();
                return;
            }
            if (received != ReceiveFrame)
            {
                failure = received == ReceiveEndOfStream ? "the seek found no frame before the end of the file" : "the decoder failed after the seek";
                seekTicks = targetTicks - BackoffTicks(attempt);
                continue;
            }

            long pts = GetFramePtsTicks(_frame);
            if (pts == long.MinValue || pts <= targetTicks || fromStart || seekTs <= startTs)
            {
                positioned = true;
                break;
            }

            // The demuxer landed after the target. Seek again further back.
            ffmpeg.av_frame_unref(_frame);
            seekTicks = targetTicks - BackoffTicks(attempt);
        }

        if (!positioned)
        {
            _paused = true;
            CompleteActiveSeek(EVideoSeekState.Failed, $"The seek to {targetTicks} ticks failed: {failure ?? "no frame"}.");
            return;
        }

        long firstPts = GetFramePtsTicks(_frame);
        if (mode == EVideoSeekMode.NearestKeyframe || firstPts == long.MinValue || firstPts > targetTicks)
        {
            // A keyframe seek gives its frame. A first frame after the target (a file that starts after it) is the result too.
            DeliverAndRelease(_frame);
            if (mode == EVideoSeekMode.NearestKeyframe)
                _paused = true;
            CompleteActiveSeek(EVideoSeekState.Completed, null);
            return;
        }

        // Exact: the frame before the first frame whose time is after the target shows at the target. The next frame time
        // decides; the frame duration is not needed, so a variable frame rate does not change the choice.
        while (true)
        {
            ffmpeg.av_frame_unref(_heldFrame);
            ffmpeg.av_frame_move_ref(_heldFrame, _frame);
            int received = ReceiveVideoFrame(seeking: true);
            if (received == ReceiveInterrupted)
            {
                ffmpeg.av_frame_unref(_heldFrame);
                StopSuperseded();
                return;
            }
            if (received == ReceiveError)
            {
                _paused = true;
                ffmpeg.av_frame_unref(_heldFrame);
                CompleteActiveSeek(EVideoSeekState.Failed, "The decoder failed during an exact seek.");
                return;
            }
            if (received == ReceiveEndOfStream)
            {
                // The file ends at or before the target, so its last frame shows there.
                _endOfStream = true;
                DeliverAndRelease(_heldFrame);
                CompleteActiveSeek(EVideoSeekState.Completed, null);
                return;
            }

            long nextPts = GetFramePtsTicks(_frame);
            if (nextPts != long.MinValue && nextPts <= targetTicks)
                continue;

            // The held frame is the result. The next frame continues the forward decode.
            DeliverAndRelease(_heldFrame);
            CompleteActiveSeek(EVideoSeekState.Completed, null);
            DeliverAndRelease(_frame);
            return;
        }
    }

    // A seek that a newer seek, a cancel, or a stop replaced. A cancel with no newer seek pauses the loop, so the frames
    // of the cancelled position do not reach the queue.
    private unsafe void StopSuperseded()
    {
        ffmpeg.av_frame_unref(_frame);
        if (!_hasPendingSeek)
            _paused = true;
        CompleteActiveSeek(EVideoSeekState.Cancelled, null);
    }

    private static long BackoffTicks(int attempt)
        => TimeSpan.TicksPerSecond << (2 * attempt);

    private unsafe void ResetDecoderAfterSeek()
    {
        ffmpeg.avcodec_flush_buffers(_videoCodecContext);
        _drainSent = false;
        _readRetryCount = 0;
        _lastVideoPtsTicks = long.MinValue;
        _syntheticVideoPtsTicks = long.MinValue;
        _videoMissingPtsStreak = 0;
        ffmpeg.av_frame_unref(_frame);
    }

    /// <summary>
    /// Receives the next video frame into <see cref="_frame"/>. It reads packets as the decoder needs them, ignores the
    /// packets of other streams, and drains the decoder at the end of the file.
    /// </summary>
    /// <param name="seeking">True while a seek runs: a newer seek, a cancel, or a stop interrupts the read.</param>
    private unsafe int ReceiveVideoFrame(bool seeking)
    {
        while (true)
        {
            int received = ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame);
            if (received >= 0)
                return ReceiveFrame;
            if (received == ffmpeg.AVERROR_EOF)
                return ReceiveEndOfStream;
            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN) || _drainSent)
                return ReceiveError;

            if (!_running || _disposed || (seeking ? IsSuperseded() : _hasPendingSeek))
                return ReceiveInterrupted;

            int read = ffmpeg.av_read_frame(_formatContext, _packet);
            if (read == ffmpeg.AVERROR_EOF)
            {
                ffmpeg.avcodec_send_packet(_videoCodecContext, null);
                _drainSent = true;
                continue;
            }
            if (read < 0)
            {
                if (++_readRetryCount > MaxReadRetries)
                {
                    OnError?.Invoke($"FFmpeg read failed after {MaxReadRetries} retries.");
                    return ReceiveError;
                }
                continue;
            }

            _readRetryCount = 0;
            try
            {
                if (_packet->stream_index == _videoStreamIndex)
                    ffmpeg.avcodec_send_packet(_videoCodecContext, _packet);
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    /// <summary>The raw presentation time of a frame in ticks, or <see cref="long.MinValue"/> if the frame has none.</summary>
    private unsafe long GetFramePtsTicks(AVFrame* frame)
    {
        long sourcePts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        if (sourcePts == ffmpeg.AV_NOPTS_VALUE)
            return long.MinValue;
        return ffmpeg.av_rescale_q(sourcePts, _formatContext->streams[_videoStreamIndex]->time_base, TicksTimeBase) - _ptsOriginTicks;
    }

    /// <summary>The duration of a frame in ticks: the frame duration of the stream, or the estimate from the frame rate.</summary>
    private unsafe long GetFrameDurationTicks(AVFrame* frame)
    {
        if (frame->duration > 0)
        {
            long ticks = ffmpeg.av_rescale_q(frame->duration, _formatContext->streams[_videoStreamIndex]->time_base, TicksTimeBase);
            if (ticks > 0)
                return ticks;
        }
        return _videoFrameDurationTicks;
    }

    /// <summary>
    /// The decode loop of the clock-driven mode. It runs seeks, decodes video frames forward, and waits at the end of the
    /// file or after a keyframe seek until the next seek or a stop.
    /// </summary>
    private unsafe void ClockDrivenLoop()
    {
        try
        {
            while (_running && !_disposed)
            {
                if (TakePendingSeek(out long target, out EVideoSeekMode mode))
                {
                    ExecuteSeek(target, mode);
                    continue;
                }

                if (_paused || _endOfStream)
                {
                    _wake.WaitOne(IdleWaitMilliseconds);
                    continue;
                }

                int received = ReceiveVideoFrame(seeking: false);
                if (received == ReceiveFrame)
                    DeliverAndRelease(_frame);
                else if (received == ReceiveEndOfStream)
                    _endOfStream = true;
                else if (received == ReceiveError)
                    _paused = true;
            }
        }
        catch (Exception ex)
        {
            LogException(ex, "FFmpeg clock-driven decode loop");
            if (_running && !_disposed)
                OnError?.Invoke($"Decode loop exception: {ex.Message}");
        }
        finally
        {
            CancelSeeks();
        }
    }

    /// <summary>Delivers a frame and then releases its reference.</summary>
    private unsafe void DeliverAndRelease(AVFrame* frame)
    {
        try
        {
            DeliverVideoFrame(frame);
        }
        finally
        {
            ffmpeg.av_frame_unref(frame);
        }
    }


    // ═══════════════════════════════════════════════════════════════
    // Decode Loop
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Main entry point for the background decode thread.
    /// Continuously reads and decodes packets until stopped or an
    /// unrecoverable error occurs.
    /// </summary>
    private void DecodeLoop()
    {
        if (_clockDriven && _isSeekable)
        {
            ClockDrivenLoop();
            return;
        }

        try
        {
            while (_running && !_disposed)
                ReadAndDecodeOnePacket();
        }
        catch (Exception ex)
        {
            LogException(ex, "FFmpeg decode loop");
            if (_running && !_disposed)
                OnError?.Invoke($"Decode loop exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads a single packet from the format context, routes it to the
    /// appropriate decoder (video or audio), and handles read errors with
    /// exponential-backoff retries.
    /// </summary>
    private unsafe void ReadAndDecodeOnePacket()
    {
        int result = ffmpeg.av_read_frame(_formatContext, _packet);
        if (result < 0)
        {
            // End of file — stop cleanly.
            if (result == ffmpeg.AVERROR_EOF)
            {
                _running = false;
                return;
            }

            // Transient read failure — retry with exponential backoff.
            _readRetryCount++;
            if (_readRetryCount > MaxReadRetries)
            {
                OnError?.Invoke($"FFmpeg read failed after {MaxReadRetries} retries.");
                _running = false;
                return;
            }

            int delayMs = Math.Min(2000, 100 * (1 << Math.Min(_readRetryCount - 1, 4)));
            Thread.Sleep(delayMs);
            return;
        }

        _readRetryCount = 0; // Reset on successful read.

        try
        {
            // Route the packet to the correct decoder based on stream index.
            if (_packet->stream_index == _videoStreamIndex)
                DecodeVideoPacket();
            else if (_packet->stream_index == _audioStreamIndex)
                DecodeAudioPacket();
        }
        finally
        {
            ffmpeg.av_packet_unref(_packet);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Video Decoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends the current video packet to the decoder and processes all
    /// output frames (a single packet may produce zero or more frames).
    /// </summary>
    private unsafe void DecodeVideoPacket()
    {
        if (_videoCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_videoCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_videoCodecContext, _frame) >= 0)
            ProcessVideoFrame();
    }

    /// <summary>Delivers the frame in <see cref="_frame"/> and then releases it.</summary>
    private unsafe void ProcessVideoFrame()
        => DeliverAndRelease(_frame);

    /// <summary>
    /// Converts a decoded video frame to RGB24, copies the pixel data to a managed byte array, computes the presentation
    /// timestamp, and delivers the result via <see cref="OnVideoFrame"/>. The caller keeps the reference of
    /// <paramref name="source"/> and releases it.
    /// </summary>
    private unsafe void DeliverVideoFrame(AVFrame* source)
    {
        // If the frame came from HW accel, transfer it to a software frame.
        AVFrame* decodedFrame = GetHwFrame(source);
        try
        {
            int width = decodedFrame->width;
            int height = decodedFrame->height;

            if (width <= 0 || height <= 0)
                return;

            // ── Notify resolution change (e.g. HLS quality switch) ──
            if (width != _lastReportedWidth || height != _lastReportedHeight)
            {
                _lastReportedWidth = width;
                _lastReportedHeight = height;
                OnVideoSizeChanged?.Invoke(width, height);
            }

            // ── Convert to RGB24 if the decoded format differs ──
            AVFrame* rgbFrame = decodedFrame;
            if (decodedFrame->format != (int)TargetPixelFormat)
            {
                var srcFmt = (AVPixelFormat)decodedFrame->format;

                // Re-create the sws scaler when dimensions or format change.
                if (_swsContext == null || width != _swsWidth || height != _swsHeight || srcFmt != _swsSrcFormat)
                {
                    if (_swsContext != null)
                        ffmpeg.sws_freeContext(_swsContext);

                    // SWS_BILINEAR = 2 (not exposed by FFmpeg.AutoGen as a named constant).
                    _swsContext = ffmpeg.sws_getContext(
                        width, height, srcFmt,
                        width, height, TargetPixelFormat,
                        2, null, null, null);
                    _swsWidth = width;
                    _swsHeight = height;
                    _swsSrcFormat = srcFmt;

                    if (_swsContext == null)
                    {
                        OnError?.Invoke($"sws_getContext failed for {srcFmt} -> {TargetPixelFormat} at {width}x{height}");
                        return;
                    }
                }

                // The RGB frame is reused. It gets a new buffer only when the frame size changes.
                rgbFrame = EnsureRgbFrame(width, height);
                if (rgbFrame == null)
                {
                    OnError?.Invoke($"Could not allocate an RGB frame of {width}x{height}.");
                    return;
                }

                ffmpeg.sws_scale(
                    _swsContext,
                    decodedFrame->data, decodedFrame->linesize,
                    0, height,
                    rgbFrame->data, rgbFrame->linesize);
            }

            // ── Copy pixel data to a managed byte array (row by row for stride safety) ──
            int dataSize = width * height * 3; // RGB24 = 3 bytes per pixel
            byte[] data = RentVideoBuffer?.Invoke(dataSize) ?? new byte[dataSize];
            fixed (byte* dest = data)
            {
                byte* src = (byte*)rgbFrame->data[0];
                int srcStride = rgbFrame->linesize[0];
                int dstStride = width * 3;
                for (int row = 0; row < height; row++)
                    Buffer.MemoryCopy(src + row * srcStride, dest + row * dstStride, dstStride, dstStride);
            }

            // ── Compute presentation timestamp (PTS) in 100ns ticks ──
            long pts = ResolveVideoPtsTicks(decodedFrame);
            long duration = GetFrameDurationTicks(decodedFrame);

            // ── Deliver the decoded frame ──
            var frame = new DecodedVideoFrame(width, height, pts, duration, VideoPixelFormat.Rgb24, new ReadOnlyMemory<byte>(data, 0, dataSize));
            OnVideoFrame?.Invoke(frame);
        }
        finally
        {
            // Free the HW-transferred frame. The caller releases the source frame.
            if (decodedFrame != source)
                ffmpeg.av_frame_free(&decodedFrame);
        }
    }

    /// <summary>Gets the reusable RGB frame with a buffer of the given size, or null if the allocation fails.</summary>
    private unsafe AVFrame* EnsureRgbFrame(int width, int height)
    {
        if (_rgbFrame != null && _rgbFrame->width == width && _rgbFrame->height == height)
            return _rgbFrame;

        if (_rgbFrame != null)
        {
            fixed (AVFrame** p = &_rgbFrame)
                ffmpeg.av_frame_free(p);
        }

        _rgbFrame = ffmpeg.av_frame_alloc();
        if (_rgbFrame == null)
            return null;
        _rgbFrame->format = (int)TargetPixelFormat;
        _rgbFrame->width = width;
        _rgbFrame->height = height;
        if (ffmpeg.av_frame_get_buffer(_rgbFrame, 0) < 0)
        {
            fixed (AVFrame** p = &_rgbFrame)
                ffmpeg.av_frame_free(p);
            return null;
        }
        return _rgbFrame;
    }

    /// <summary>
    /// If the decoded frame is in a hardware pixel format, transfers it to
    /// a software frame. Otherwise returns the original frame unchanged.
    /// </summary>
    private unsafe AVFrame* GetHwFrame(AVFrame* frame)
    {
        if (frame->format != (int)_hwPixelFormat)
            return frame;

        var swFrame = ffmpeg.av_frame_alloc();
        if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0)
        {
            ffmpeg.av_frame_free(&swFrame);
            return frame; // Fallback: return original on transfer failure.
        }

        ffmpeg.av_frame_unref(frame);
        return swFrame;
    }

    // ═══════════════════════════════════════════════════════════════
    // Audio Decoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Sends the current audio packet to the decoder and processes all
    /// output frames.
    /// </summary>
    private unsafe void DecodeAudioPacket()
    {
        if (_audioCodecContext == null)
            return;

        if (ffmpeg.avcodec_send_packet(_audioCodecContext, _packet) < 0)
            return;

        while (ffmpeg.avcodec_receive_frame(_audioCodecContext, _frame) >= 0)
            ProcessAudioFrame();
    }

    /// <summary>
    /// Converts a decoded audio frame from its native sample format
    /// (possibly planar) to interleaved PCM, computes the PTS, and
    /// delivers it via <see cref="OnAudioFrame"/>.
    /// </summary>
    private unsafe void ProcessAudioFrame()
    {
        int channels = _audioCodecContext->ch_layout.nb_channels;
        int sampleRate = _audioCodecContext->sample_rate;
        int nbSamples = _frame->nb_samples;

        if (channels <= 0 || sampleRate <= 0 || nbSamples <= 0)
            return;

        // ── Map FFmpeg sample format to our AudioSampleFormat enum ──
        AVSampleFormat fmt = (AVSampleFormat)_frame->format;
        AudioSampleFormat sampleFormat;
        int bytesPerSample;

        switch (fmt)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                sampleFormat = AudioSampleFormat.S16;
                bytesPerSample = 2;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S32:
            case AVSampleFormat.AV_SAMPLE_FMT_S32P:
                sampleFormat = AudioSampleFormat.S32;
                bytesPerSample = 4;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                sampleFormat = AudioSampleFormat.F32;
                bytesPerSample = 4;
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_DBL:
            case AVSampleFormat.AV_SAMPLE_FMT_DBLP:
                sampleFormat = AudioSampleFormat.F64;
                bytesPerSample = 8;
                break;
            default:
                // Unsupported sample format — skip this frame.
                ffmpeg.av_frame_unref(_frame);
                return;
        }

        int totalSamples = nbSamples * channels;
        int totalBytes = totalSamples * bytesPerSample;
        byte[] data = new byte[totalBytes];

        bool isPlanar = ffmpeg.av_sample_fmt_is_planar(fmt) != 0;

        // ── Copy audio data, interleaving planar formats ──
        fixed (byte* dest = data)
        {
            if (!isPlanar)
            {
                // Interleaved layout — straight memory copy.
                Buffer.MemoryCopy((void*)_frame->data[0], dest, totalBytes, totalBytes);
            }
            else
            {
                // Planar layout — each channel is in a separate data[] plane.
                // Interleave sample-by-sample: [L0 R0 L1 R1 ...].
                for (int s = 0; s < nbSamples; s++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        byte* src = (byte*)_frame->data[(uint)ch] + s * bytesPerSample;
                        byte* dst = dest + (s * channels + ch) * bytesPerSample;
                        Buffer.MemoryCopy(src, dst, bytesPerSample, bytesPerSample);
                    }
                }
            }
        }

        // ── Compute presentation timestamp in .NET ticks ──
        long pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? _frame->best_effort_timestamp
            : _frame->pts;

        if (_audioStreamIndex >= 0 && pts != ffmpeg.AV_NOPTS_VALUE)
        {
            unsafe
            {
                AVRational tb = _formatContext->streams[_audioStreamIndex]->time_base;
                pts = pts * TimeSpan.TicksPerSecond * tb.num / tb.den;
            }
        }

        // ── Deliver the decoded audio frame ──
        var frame = new DecodedAudioFrame(sampleRate, channels, sampleFormat, pts, data);
        OnAudioFrame?.Invoke(frame);

        ffmpeg.av_frame_unref(_frame);
    }

    // ═══════════════════════════════════════════════════════════════
    // Initialization — Stream Opening & Codec Setup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Allocates and opens the FFmpeg format context for the given URL,
    /// discovers video/audio streams, and opens their respective codecs.
    /// </summary>
    /// <returns><c>true</c> if at least one stream was successfully opened.</returns>
    private unsafe bool AllocateAndOpen(string url, StreamOpenOptions? options)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            OnError?.Invoke("Cannot open stream: URL is null or empty.");
            return false;
        }

        _formatContext = ffmpeg.avformat_alloc_context();
        if (_formatContext == null)
        {
            OnError?.Invoke("Failed to allocate AVFormatContext.");
            return false;
        }

        AVDictionary* dict = null;
        try
        {
            // ── Probe / analysis budget (generous sizing matching Flyleaf defaults) ──
            ffmpeg.av_dict_set(&dict, "probesize", (50L * 1024 * 1024).ToString(), 0);       // 50 MB
            ffmpeg.av_dict_set(&dict, "analyzeduration", (10L * 1_000_000).ToString(), 0);    // 10 seconds

            // ── HTTP options from caller ──
            if (!string.IsNullOrWhiteSpace(options?.UserAgent))
                ffmpeg.av_dict_set(&dict, "user_agent", options!.UserAgent, 0);

            if (!string.IsNullOrWhiteSpace(options?.Referrer))
                ffmpeg.av_dict_set(&dict, "referer", options!.Referrer, 0);

            if (options?.Headers is { Count: > 0 })
            {
                string headerBlock = string.Join("\r\n", options.Headers.Select(static h => $"{h.Key}: {h.Value}"));
                if (!string.IsNullOrWhiteSpace(headerBlock))
                    ffmpeg.av_dict_set(&dict, "headers", headerBlock + "\r\n", 0);
            }

            // ── HLS-specific options ──
            bool hls = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                     || url.Contains("hls", StringComparison.OrdinalIgnoreCase);

            // NOTE: Do NOT set protocol_whitelist. Flyleaf's FFmpeg build includes
            // the HLS demuxer which manages its own protocol whitelist internally
            // (file,http,https,tcp,tls,crypto). Overriding it blocks crypto and
            // data protocols needed for encrypted or embedded HLS segments.

            if (hls)
            {
                // Disable HTTP keep-alive for HLS — prevents stale connections
                // when the demuxer opens many short-lived segment requests.
                ffmpeg.av_dict_set(&dict, "http_persistent", "0", 0);
            }

            // ── Reconnect policy ──
            if (options?.EnableReconnect == true)
            {
                ffmpeg.av_dict_set(&dict, "reconnect", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_streamed", "1", 0);
                ffmpeg.av_dict_set(&dict, "reconnect_delay_max", "5", 0);
            }

            // Discard corrupt packets rather than passing them to the decoder.
            ffmpeg.av_dict_set(&dict, "fflags", "discardcorrupt", 0);

            if (options?.OpenTimeoutMs > 0)
                ffmpeg.av_dict_set(&dict, "timeout", (options.OpenTimeoutMs * 1000).ToString(), 0);

            // ── Open the format context ──
            // Let FFmpeg auto-detect format (including HLS) — do NOT force
            // av_find_input_format("hls"). Auto-detection works correctly
            // with this FFmpeg build.
            AVFormatContext* pFmt = _formatContext;
            LogInfo($"Streaming FFmpeg: avformat_open_input url='{url}' hls={hls}");

            int result;
            try
            {
                result = ffmpeg.avformat_open_input(&pFmt, url, null, &dict);
            }
            catch (Exception ex)
            {
                // avformat_open_input can throw a native SEH / access-violation
                // exception instead of returning < 0. Treat it as an open failure
                // and let the caller handle cleanup.
                _formatContext = pFmt; // preserve whatever FFmpeg set
                OnError?.Invoke($"Native exception in avformat_open_input: {ex.Message}");
                return false;
            }
            _formatContext = pFmt;

            if (result < 0)
            {
                string err = GetFFmpegError(result);
                OnError?.Invoke($"Failed to open stream: {err} (code {result})");
                return false;
            }

            LogInfo("Streaming FFmpeg: avformat_open_input succeeded.");
        }
        finally
        {
            ffmpeg.av_dict_free(&dict);
        }

        // ── Analyze stream info ──
        LogInfo("Streaming FFmpeg: avformat_find_stream_info...");
        if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
        {
            OnError?.Invoke("Failed to find stream info.");
            return false;
        }

        string? fmtName = _formatContext->iformat != null
            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((nint)_formatContext->iformat->name)
            : null;
        long durationUs = _formatContext->duration;
        LogInfo($"Streaming FFmpeg: format='{fmtName}', nb_streams={_formatContext->nb_streams}, duration={durationUs}us");

        // ── Select best video + audio streams ──
        // Try FFmpeg's heuristic first, then fall back to first-discovered.
        _videoStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        _audioStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);

        if (_videoStreamIndex < 0 || _audioStreamIndex < 0)
        {
            for (int i = 0; i < _formatContext->nb_streams; i++)
            {
                var codecpar = _formatContext->streams[i]->codecpar;
                if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoStreamIndex < 0)
                    _videoStreamIndex = i;
                else if (codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && _audioStreamIndex < 0)
                    _audioStreamIndex = i;
            }
        }

        LogInfo($"Streaming FFmpeg: videoStream={_videoStreamIndex}, audioStream={_audioStreamIndex}");

        if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
        {
            OnError?.Invoke("No video or audio stream found.");
            return false;
        }

        // ── Open codecs for the selected streams ──
        if (_videoStreamIndex >= 0)
            OpenVideoCodec();

        if (_audioStreamIndex >= 0 && _decodeAudio)
            OpenAudioCodec();
        else
            _audioStreamIndex = -1;

        // Allocate reusable frame and packet buffers for the decode loop.
        _frame = ffmpeg.av_frame_alloc();
        _heldFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();

        _isSeekable = IsSeekableSource(url, fmtName, durationUs);
        _ptsOriginTicks = 0;
        if (_clockDriven && _videoStreamIndex >= 0)
        {
            AVStream* videoStream = _formatContext->streams[_videoStreamIndex];
            long start = videoStream->start_time != ffmpeg.AV_NOPTS_VALUE ? videoStream->start_time : 0L;
            _ptsOriginTicks = ffmpeg.av_rescale_q(start, videoStream->time_base, TicksTimeBase);
        }
        LogInfo($"Streaming FFmpeg: seekable={_isSeekable}");
        return true;
    }

    /// <summary>
    /// Opens and configures the video codec for the selected video stream.
    /// Detects frame rate from stream metadata and reports it via
    /// <see cref="OnVideoFrameRateDetected"/>.
    /// </summary>
    private unsafe void OpenVideoCodec()
    {
        AVStream* stream = _formatContext->streams[_videoStreamIndex];
        var codecpar = _formatContext->streams[_videoStreamIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            OnError?.Invoke($"Unsupported video codec: {codecpar->codec_id}");
            _videoStreamIndex = -1;
            return;
        }

        _videoCodecContext = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_videoCodecContext, codecpar);

        // Use 4-thread frame-level parallelism for software decode.
        _videoCodecContext->thread_count = 4;
        _videoCodecContext->thread_type = ffmpeg.FF_THREAD_FRAME;

        // Hardware acceleration disabled: Flyleaf's D3D11 HW frames are incompatible
        // with our OpenGL pipeline, and CUDA/D3D11VA caused ucrtbase.dll native crashes.
        // Pure software decode is sufficient for streaming video.

        int result = ffmpeg.avcodec_open2(_videoCodecContext, codec, null);
        if (result < 0)
        {
            OnError?.Invoke($"Could not open video codec: {GetFFmpegError(result)}");
            fixed (AVCodecContext** p = &_videoCodecContext)
                ffmpeg.avcodec_free_context(p);
            _videoCodecContext = null;
            _videoStreamIndex = -1;
            return;
        }

        // Detect frame rate from multiple sources (avg > real > codec).
        double? detectedFps = TryGetFrameRate(stream->avg_frame_rate)
            ?? TryGetFrameRate(stream->r_frame_rate)
            ?? TryGetFrameRate(_videoCodecContext->framerate);

        if (detectedFps is > 0)
        {
            OnVideoFrameRateDetected?.Invoke(detectedFps.Value);
            LogInfo($"Streaming FFmpeg: detected video fps={detectedFps.Value:F3}");

            long frameTicks = (long)Math.Round(TimeSpan.TicksPerSecond / detectedFps.Value);
            _videoFrameDurationTicks = Math.Clamp(frameTicks,
                TimeSpan.TicksPerMillisecond,
                TimeSpan.TicksPerSecond);
        }
    }

    /// <summary>
    /// Opens and configures the audio codec for the selected audio stream.
    /// </summary>
    private unsafe void OpenAudioCodec()
    {
        var codecpar = _formatContext->streams[_audioStreamIndex]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        if (codec == null)
        {
            OnError?.Invoke($"Unsupported audio codec: {codecpar->codec_id}");
            _audioStreamIndex = -1;
            return;
        }

        _audioCodecContext = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_audioCodecContext, codecpar);

        int result = ffmpeg.avcodec_open2(_audioCodecContext, codec, null);
        if (result < 0)
        {
            OnError?.Invoke($"Could not open audio codec: {GetFFmpegError(result)}");
            fixed (AVCodecContext** p = &_audioCodecContext)
                ffmpeg.avcodec_free_context(p);
            _audioCodecContext = null;
            _audioStreamIndex = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Hardware Acceleration (currently disabled — retained for future use)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to initialize hardware-accelerated decoding for the given
    /// device type. Currently unused because HW accel is incompatible with
    /// the OpenGL rendering pipeline.
    /// </summary>
    /// <returns><c>true</c> if HW accel was successfully initialized.</returns>
    private unsafe bool TryInitHwAccel(AVCodecContext* ctx, AVHWDeviceType type)
    {
        fixed (AVBufferRef** p = &_hwDeviceContext)
        {
            if (ffmpeg.av_hwdevice_ctx_create(p, type, null, null, 0) < 0)
                return false;
        }

        ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceContext);

        // Search for a matching HW config that supports the requested device type.
        for (int i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(ctx->codec, i);
            if (config == null)
                break;

            if ((config->methods & 1) != 0 && config->device_type == type)
            {
                _hwPixelFormat = config->pix_fmt;
                return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Cleanup — Native Resource Deallocation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Frees all allocated FFmpeg native resources in the correct order:
    /// scaler → frame/packet → codecs → HW device → format context.
    /// </summary>
    private unsafe void CleanupUnsafe()
    {
        _running = false;

        _lastVideoPtsTicks = long.MinValue;
        _syntheticVideoPtsTicks = long.MinValue;
        _videoMissingPtsStreak = 0;

        // Free the software scaler context.
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        // Free the reusable frame buffers.
        if (_frame != null)
        {
            fixed (AVFrame** p = &_frame)
                ffmpeg.av_frame_free(p);
        }

        if (_heldFrame != null)
        {
            fixed (AVFrame** p = &_heldFrame)
                ffmpeg.av_frame_free(p);
        }

        if (_rgbFrame != null)
        {
            fixed (AVFrame** p = &_rgbFrame)
                ffmpeg.av_frame_free(p);
        }

        _isSeekable = false;

        // Free the reusable packet buffer.
        if (_packet != null)
        {
            fixed (AVPacket** p = &_packet)
                ffmpeg.av_packet_free(p);
        }

        // Free codec contexts.
        if (_videoCodecContext != null)
        {
            fixed (AVCodecContext** p = &_videoCodecContext)
                ffmpeg.avcodec_free_context(p);
        }

        if (_audioCodecContext != null)
        {
            fixed (AVCodecContext** p = &_audioCodecContext)
                ffmpeg.avcodec_free_context(p);
        }

        // Free the HW device context (if any).
        if (_hwDeviceContext != null)
        {
            fixed (AVBufferRef** p = &_hwDeviceContext)
                ffmpeg.av_buffer_unref(p);
        }

        // Close the format context (also frees stream info).
        if (_formatContext != null)
        {
            fixed (AVFormatContext** p = &_formatContext)
                ffmpeg.avformat_close_input(p);
        }
    }

    /// <summary>
    /// Converts FFmpeg video timestamps to a monotonic .NET-tick timeline.
    /// When FFmpeg timestamps are missing or non-monotonic, synthesizes PTS
    /// using the estimated frame duration so downstream A/V sync logic can
    /// stay on audio-clock pacing.
    /// </summary>
    private unsafe long ResolveVideoPtsTicks(AVFrame* decodedFrame)
    {
        long sourcePts = decodedFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? decodedFrame->best_effort_timestamp
            : decodedFrame->pts;

        long resolvedPts = long.MinValue;
        bool hasResolvedPts = false;

        if (_videoStreamIndex >= 0 && sourcePts != ffmpeg.AV_NOPTS_VALUE)
        {
            AVRational tb = _formatContext->streams[_videoStreamIndex]->time_base;
            AVRational dstTb = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
            resolvedPts = ffmpeg.av_rescale_q(sourcePts, tb, dstTb) - _ptsOriginTicks;

            // In the clock-driven mode the stream start is zero, and a frame before it (pre-roll or an edit list) keeps its
            // negative time, so a keyframe seek never reports a time after its target.
            hasResolvedPts = _clockDriven || resolvedPts >= 0;
        }

        // Reject non-monotonic timestamps; they break hasValidPts checks in
        // the session and force wall-clock fallback.
        if (hasResolvedPts && _lastVideoPtsTicks != long.MinValue && resolvedPts <= _lastVideoPtsTicks)
            hasResolvedPts = false;

        if (hasResolvedPts)
        {
            _videoMissingPtsStreak = 0;
            _syntheticVideoPtsTicks = resolvedPts;
            _lastVideoPtsTicks = resolvedPts;
            return resolvedPts;
        }

        // Missing/invalid timestamp: synthesize a monotonic PTS.
        _videoMissingPtsStreak++;

        long step = Math.Max(TimeSpan.TicksPerMillisecond, _videoFrameDurationTicks);
        if (_syntheticVideoPtsTicks == long.MinValue)
        {
            _syntheticVideoPtsTicks = _lastVideoPtsTicks != long.MinValue
                ? _lastVideoPtsTicks + step
                : step;
        }
        else
        {
            _syntheticVideoPtsTicks += step;
        }

        if (_videoMissingPtsStreak == 1 || _videoMissingPtsStreak % 300 == 0)
        {
            LogInfo($"[AV Video] Synthesizing PTS for frame(s): streak={_videoMissingPtsStreak}, " +
                    $"stepMs={step / (double)TimeSpan.TicksPerMillisecond:F2}");
        }

        _lastVideoPtsTicks = _syntheticVideoPtsTicks;
        return _syntheticVideoPtsTicks;
    }

    // ═══════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// True if a seek can move the opened source: a local file (no network scheme) that is not an HLS playlist, with a
    /// known duration, a seekable IO context, and a video stream. A live stream has no duration, so it gives false.
    /// </summary>
    private unsafe bool IsSeekableSource(string url, string? formatName, long durationUs)
    {
        if (_videoStreamIndex < 0 || _videoCodecContext == null)
            return false;
        if (durationUs <= 0 || durationUs == ffmpeg.AV_NOPTS_VALUE)
            return false;
        if (formatName is not null && formatName.Contains("hls", StringComparison.OrdinalIgnoreCase))
            return false;
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0 && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;
        // A file IO context reports its size. A stream with no seek reports an error code.
        AVIOContext* io = _formatContext->pb;
        return io != null && ffmpeg.avio_size(io) > 0;
    }

    /// <summary>
    /// Validates an <see cref="AVRational"/> frame rate and returns it as
    /// a <c>double</c>, or <c>null</c> if the value is out of the sane
    /// range (1–240 fps).
    /// </summary>
    private static double? TryGetFrameRate(AVRational rate)
    {
        if (rate.num <= 0 || rate.den <= 0)
            return null;

        double fps = (double)rate.num / rate.den;
        return fps is >= 1.0 and <= 240.0 ? fps : null;
    }

    /// <summary>
    /// Converts an FFmpeg error code into a human-readable error string.
    /// </summary>
    private static unsafe string GetFFmpegError(int errorCode)
    {
        byte[] buf = new byte[1024];
        fixed (byte* ptr = buf)
        {
            ffmpeg.av_strerror(errorCode, ptr, (ulong)buf.Length);
            return new string((sbyte*)ptr).Trim('\0');
        }
    }

    private static void LogInfo(string message)
        => Trace.WriteLine(message);

    private static void LogWarning(string message)
        => Trace.TraceWarning(message);

    private static void LogException(Exception ex, string context)
    {
        Trace.TraceError($"{context}: {ex.Message}");
        Trace.WriteLine(ex.ToString());
    }
}
