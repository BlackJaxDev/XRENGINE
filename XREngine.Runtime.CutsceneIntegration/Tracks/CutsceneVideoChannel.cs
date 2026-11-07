using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Cutscenes;

/// <summary>
/// The playback state of one video track: the media session of the active clip, the frame cache, the seek requests, and
/// the frame target. The update thread uses it. The session decodes on its own thread, so no member waits for a decode.
/// </summary>
/// <remarks>
/// <para>
/// Each update first looks in the cache for the frame that shows at the wanted time. A hit shows the frame and starts no
/// decode. A miss asks the residency backend for a copy, and then asks the session for a seek: a keyframe seek during a
/// drag, an exact seek when the playhead stops or jumps. A newer request supersedes an older one in the session, so only
/// the newest position decodes. The first frame after a completed seek is its result; it shows even when it is not in the
/// cache range, for example a keyframe before the time or the first frame of a file that starts after zero.
/// </para>
/// <para>
/// Forward playback does not seek for each frame. After one exact seek, the decoder runs forward, and the cutscene time
/// releases the frames through <see cref="IMediaStreamSession.TryDequeueVideoFrame"/>. The channel seeks again only after a
/// jump, a move before the decoded range, or when the decoder falls more than <see cref="MaxDecoderLagTicks"/> behind.
/// </para>
/// </remarks>
internal sealed class CutsceneVideoChannel
{
    /// <summary>The distance in ticks by which forward playback can pass the last decoded frame before the channel seeks again.</summary>
    public const long MaxDecoderLagTicks = TimeSpan.TicksPerSecond;

    /// <summary>The number of frames that the session queues ahead of the cutscene time.</summary>
    public const int SessionQueueCapacity = 4;

    // A clock that releases the first queued frame whatever its time.
    private const long ReleaseAnyFrame = long.MaxValue / 4;

    private readonly Func<IMediaStreamSession> _createSession;
    private readonly Action<CutsceneVideoChannel, ECutsceneVideoFailure, string> _fail;

    private Task<IMediaStreamSession>? _openTask;
    private IMediaStreamSession? _session;
    private bool _clipFailed;

    private bool _hasRequest;
    private long _requestTicks;
    private ECutsceneVideoMode _requestMode;
    private Task? _requestTask;
    private bool _awaitingResult;
    private bool _hasResult;
    private long _resultPts;

    private bool _hasKeyframe;
    private long _keyframePts;
    private long _keyframeTarget;

    private bool _decoderForward;
    private long _forwardBaseTicks;
    private long _lastDequeuedPts = long.MinValue;

    private bool _hasPresented;
    private long _presentedPts;
    private int _preallocatedBytes;
    private byte[]? _copyScratch;

    /// <summary>Creates a channel for one video track.</summary>
    /// <param name="trackIndex">The index of the track in the cutscene.</param>
    /// <param name="track">The track.</param>
    /// <param name="createSession">Creates a media session. It runs on a background task.</param>
    /// <param name="fail">Receives each failure of a clip. The evaluator reports it once.</param>
    public CutsceneVideoChannel(int trackIndex, VideoTrack track, Func<IMediaStreamSession> createSession, Action<CutsceneVideoChannel, ECutsceneVideoFailure, string> fail)
    {
        TrackIndex = trackIndex;
        Track = track;
        _createSession = createSession;
        _fail = fail;
        Cache = new VideoFrameCache(Math.Max(1, track.FrameCacheCapacity));
    }

    /// <summary>The index of the track in the cutscene.</summary>
    public int TrackIndex { get; }

    /// <summary>The track.</summary>
    public VideoTrack Track { get; }

    /// <summary>The frame cache of the active clip.</summary>
    public VideoFrameCache Cache { get; }

    /// <summary>The index of the open clip, or -1.</summary>
    public int ClipIndex { get; private set; } = -1;

    /// <summary>The full path of the file of the open clip, or null.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>The surface that shows the frames, or null.</summary>
    public IVideoFrameTarget? Target { get; private set; }

    /// <summary>The stream time in ticks that the last evaluation wants.</summary>
    public long DesiredTicks { get; private set; }

    /// <summary>The mode of the last evaluation.</summary>
    public ECutsceneVideoMode Mode { get; private set; }

    /// <summary>The number of seeks that the channel asked the session for.</summary>
    public int SeekRequestCount { get; private set; }

    /// <summary>The number of frames that the channel gave to its target.</summary>
    public int PresentedFrameCount { get; private set; }

    /// <summary>The time of the frame that the target shows now, or null.</summary>
    public long? PresentedPts => _hasPresented ? _presentedPts : null;

    /// <summary>True when the media session of the clip is open.</summary>
    public bool IsSessionOpen => _session is not null;

    /// <summary>Sets the surface. A new surface clears the old one and gets the current frame at the next update.</summary>
    /// <param name="target">The surface.</param>
    public void Bind(IVideoFrameTarget target)
    {
        if (ReferenceEquals(Target, target))
            return;
        Target?.ClearFrame();
        Target = target;
        _hasPresented = false;
    }

    /// <summary>Opens the file of a clip on a background task. The previous session closes and the cache clears.</summary>
    /// <param name="clipIndex">The index of the clip.</param>
    /// <param name="fullPath">The full path of the file.</param>
    public void Open(int clipIndex, string fullPath)
    {
        CloseSession();
        ClipIndex = clipIndex;
        SourcePath = fullPath;
        Func<IMediaStreamSession> create = _createSession;
        _openTask = Task.Run(async () =>
        {
            IMediaStreamSession session = create();
            try
            {
                StreamOpenOptions options = new()
                {
                    ClockDrivenPresentation = true,
                    DecodeAudio = false,
                    VideoQueueCapacity = SessionQueueCapacity,
                    EnableReconnect = false,
                };
                await session.OpenAsync(fullPath, options, CancellationToken.None).ConfigureAwait(false);
                return session;
            }
            catch
            {
                CloseInBackground(session);
                throw;
            }
        });
    }

    /// <summary>Sets the time and the mode that the next update shows.</summary>
    /// <param name="ticks">The stream time in ticks.</param>
    /// <param name="mode">How to get the frame.</param>
    /// <param name="discontinuity">True after a jump, so forward playback must seek again.</param>
    public void SetDesired(long ticks, ECutsceneVideoMode mode, bool discontinuity)
    {
        DesiredTicks = ticks;
        Mode = mode;
        if (discontinuity)
            _decoderForward = false;
    }

    /// <summary>A drag stopped: the next update decodes the exact frame of the last time.</summary>
    public void StopDrag()
    {
        if (Mode == ECutsceneVideoMode.Keyframe)
            Mode = ECutsceneVideoMode.Exact;
    }

    /// <summary>
    /// Collects a completed open or seek, moves the decoded frames to the cache, shows the frame of the wanted time, and
    /// asks for a seek when the cache does not have it.
    /// </summary>
    /// <param name="residency">The video residency backend, or null.</param>
    public void Update(VideoResidencyBackend? residency)
    {
        if (ClipIndex < 0 || _clipFailed || !TryGetSession(out IMediaStreamSession? session))
            return;
        if (!CheckRequest())
            return;

        Drain(session!);

        long ticks = DesiredTicks;
        if (Cache.TryGetFrame(ticks, out DecodedVideoFrame frame) || (residency is not null && SourcePath is not null && residency.TryCopyFrame(SourcePath, ticks, Cache, ref _copyScratch) && Cache.TryGetFrame(ticks, out frame)))
        {
            Present(frame);
            if (Mode == ECutsceneVideoMode.Sequential && NeedsForwardSeek(ticks))
                Request(session!, ticks, ECutsceneVideoMode.Exact);
            return;
        }

        if (Mode == ECutsceneVideoMode.Keyframe && _hasKeyframe && _keyframePts <= ticks && ticks <= _keyframeTarget && Cache.TryGetFrameAt(_keyframePts, out frame))
        {
            // The keyframe of an earlier target in this range is also the keyframe at or before this time.
            Present(frame);
            return;
        }

        // The result of the request for this time shows, if the request gives the accuracy that the mode needs: a drag
        // takes any result, and the other modes need an exact result.
        if (_hasResult && _hasRequest && _requestTicks == ticks && (Mode == ECutsceneVideoMode.Keyframe || _requestMode == ECutsceneVideoMode.Exact)
            && Cache.TryGetFrameAt(_resultPts, out frame))
        {
            Present(frame);
            return;
        }

        switch (Mode)
        {
            case ECutsceneVideoMode.Sequential:
                if (NeedsForwardSeek(ticks))
                    Request(session!, ticks, ECutsceneVideoMode.Exact);
                break;
            case ECutsceneVideoMode.Keyframe:
                Request(session!, ticks, ECutsceneVideoMode.Keyframe);
                break;
            default:
                Request(session!, ticks, ECutsceneVideoMode.Exact);
                break;
        }
    }

    /// <summary>Clears the target, closes the session in the background, and forgets the clip.</summary>
    public void Release()
    {
        Target?.ClearFrame();
        Target = null;
        CloseSession();
        ClipIndex = -1;
        SourcePath = null;
        _clipFailed = false;
    }

    /// <summary>Closes the session of the clip and stops the clip. The target keeps the last frame until a release.</summary>
    public void Fail()
    {
        _clipFailed = true;
        CloseSession();
    }

    private bool TryGetSession(out IMediaStreamSession? session)
    {
        session = _session;
        if (session is not null)
            return true;
        if (_openTask is not { IsCompleted: true } openTask)
            return false;

        _openTask = null;
        if (!openTask.IsCompletedSuccessfully)
        {
            Exception? error = openTask.Exception?.GetBaseException();
            Fail();
            _fail(this, ECutsceneVideoFailure.OpenFailed, error?.Message ?? "the open was cancelled");
            return false;
        }

        session = openTask.Result;
        if (!session.CanSeek)
        {
            CloseInBackground(session);
            session = null;
            Fail();
            _fail(this, ECutsceneVideoFailure.CannotSeek, "the source does not support a seek (a live stream, a network stream, or an HLS playlist). A video track plays only local files");
            return false;
        }

        _session = session;
        return true;
    }

    // Collects the result of the running seek. A cancelled seek was superseded. A failed seek stops the clip.
    private bool CheckRequest()
    {
        if (_requestTask is not { IsCompleted: true } task)
            return true;

        _requestTask = null;
        if (task.IsCanceled)
        {
            _awaitingResult = false;
            return true;
        }
        if (task.IsFaulted)
        {
            Exception? error = task.Exception?.GetBaseException();
            Fail();
            _fail(this, ECutsceneVideoFailure.SeekFailed, error?.Message ?? "unknown error");
            return false;
        }
        return true;
    }

    // Moves the released frames to the cache. The first frame after a completed seek is its result.
    private void Drain(IMediaStreamSession session)
    {
        if (_awaitingResult && _requestTask is null && session.TryDequeueVideoFrame(ReleaseAnyFrame, out DecodedVideoFrame result))
        {
            _awaitingResult = false;
            Accept(result);
            _hasResult = true;
            _resultPts = result.PresentationTimestampTicks;
            if (_requestMode == ECutsceneVideoMode.Keyframe)
            {
                _hasKeyframe = true;
                _keyframePts = result.PresentationTimestampTicks;
                _keyframeTarget = _requestTicks;
            }
        }

        // A keyframe seek pauses the decoder, and the frames of a running seek are not released yet.
        if (_awaitingResult || !_decoderForward)
            return;
        while (session.TryDequeueVideoFrame(DesiredTicks, out DecodedVideoFrame frame))
            Accept(frame);
    }

    private void Accept(in DecodedVideoFrame frame)
    {
        // The first frame gives the frame size, so all slot buffers are made at once. Later frames of the same size then
        // allocate nothing, also while the cache fills.
        if (frame.PackedData.Length > _preallocatedBytes)
        {
            Cache.Preallocate(frame.PackedData.Length);
            _preallocatedBytes = frame.PackedData.Length;
        }
        Cache.Insert(frame);
        _lastDequeuedPts = frame.PresentationTimestampTicks;
    }

    // Forward playback seeks only when the decoder is not running forward, when the time is before the decoded range, or
    // when the decoder fell far behind.
    private bool NeedsForwardSeek(long ticks)
    {
        if (_hasRequest && _requestMode == ECutsceneVideoMode.Exact && (_requestTask is not null || _awaitingResult))
            return false;
        if (!_decoderForward)
            return true;
        if (ticks < _forwardBaseTicks)
            return true;
        return _lastDequeuedPts != long.MinValue && ticks > _lastDequeuedPts + MaxDecoderLagTicks;
    }

    private void Request(IMediaStreamSession session, long ticks, ECutsceneVideoMode mode)
    {
        if (_hasRequest && _requestTicks == ticks && _requestMode == mode && (_requestTask is not null || _awaitingResult || _hasResult))
            return;

        _hasRequest = true;
        _requestTicks = ticks;
        _requestMode = mode;
        _awaitingResult = true;
        _hasResult = false;
        _decoderForward = mode == ECutsceneVideoMode.Exact;
        _forwardBaseTicks = ticks;
        _lastDequeuedPts = long.MinValue;
        SeekRequestCount++;
        _requestTask = session.SeekAsync(ticks, mode == ECutsceneVideoMode.Keyframe ? EVideoSeekMode.NearestKeyframe : EVideoSeekMode.Exact, CancellationToken.None);
    }

    private void Present(in DecodedVideoFrame frame)
    {
        if (Target is not { } target)
            return;
        if (_hasPresented && _presentedPts == frame.PresentationTimestampTicks)
            return;
        target.SubmitFrame(frame);
        _hasPresented = true;
        _presentedPts = frame.PresentationTimestampTicks;
        PresentedFrameCount++;
    }

    private void CloseSession()
    {
        if (_openTask is { } openTask)
        {
            _openTask = null;
            openTask.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                    CloseInBackground(t.Result);
            }, TaskScheduler.Default);
        }
        if (_session is { } session)
        {
            _session = null;
            CloseInBackground(session);
        }

        Cache.Clear();
        _hasRequest = false;
        _requestTask = null;
        _awaitingResult = false;
        _hasResult = false;
        _hasKeyframe = false;
        _decoderForward = false;
        _lastDequeuedPts = long.MinValue;
        _hasPresented = false;
    }

    // A close joins the decode thread, so it runs off the update thread.
    private static void CloseInBackground(IMediaStreamSession session)
        => Task.Run(() =>
        {
            session.Close();
            session.Dispose();
        });
}
