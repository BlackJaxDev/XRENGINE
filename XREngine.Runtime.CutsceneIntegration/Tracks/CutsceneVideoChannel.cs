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
/// drag, an exact seek when the playhead stops or jumps. A drag stops when the wanted time stays the same for
/// <see cref="DragDwellSeconds"/> of wall time; the editor sends a scrub seek in each frame while the ruler is held, so a
/// tick with a seek is not a move by itself.
/// </para>
/// <para>
/// At most one seek is in flight. While it runs, newer times wait, and the channel asks for the newest time when the seek
/// ends. The seek uses <see cref="IMediaStreamSession.RequestSeek"/> and a polled state, so a drag allocates nothing. The
/// first frame after a completed seek is its result; it shows even when it is not in the cache range, for example a
/// keyframe before the time or the first frame of a file that starts after zero.
/// </para>
/// <para>
/// Forward playback does not seek for each frame. After one exact seek, the decoder runs forward, and the cutscene time
/// releases the frames through <see cref="IMediaStreamSession.TryDequeueVideoFrame"/>. The channel seeks again only after a
/// jump, a move before the decoded range, or when the decoder falls more than <see cref="MaxDecoderLagTicks"/> behind. After
/// the end of the file it does not seek again.
/// </para>
/// </remarks>
internal sealed class CutsceneVideoChannel
{
    /// <summary>The distance in ticks by which forward playback can pass the last decoded frame before the channel seeks again.</summary>
    public const long MaxDecoderLagTicks = TimeSpan.TicksPerSecond;

    /// <summary>The number of frames that the session queues ahead of the cutscene time.</summary>
    public const int SessionQueueCapacity = 2;

    /// <summary>
    /// The wall time in seconds for which the wanted time of a drag must stay the same before the channel decodes the exact
    /// frame. It is the dwell time of the residency planner.
    /// </summary>
    public const double DragDwellSeconds = CutsceneResidencyPlanner.DefaultDwellSeconds;

    // A clock that releases the first queued frame whatever its time.
    private const long ReleaseAnyFrame = long.MaxValue / 4;

    private readonly Func<IMediaStreamSession> _createSession;
    private readonly Action<CutsceneVideoChannel, ECutsceneVideoFailure, string> _fail;
    private readonly TimeProvider _time;
    private readonly long _dragDwellTimestamps;

    private Task<IMediaStreamSession>? _openTask;
    private IMediaStreamSession? _session;
    private bool _clipFailed;

    private int _requestId = -1;
    private bool _requestInFlight;
    private long _requestTicks;
    private ECutsceneVideoMode _requestMode;
    private bool _awaitingResult;
    private bool _hasResult;
    private long _resultPts;

    private bool _hasKeyframe;
    private long _keyframePts;
    private long _keyframeTarget;

    private bool _decoderForward;
    private long _forwardBaseTicks;
    private long _lastDequeuedPts = long.MinValue;
    private long _fileEndPts = long.MaxValue;

    private long _dragTicks = long.MinValue;
    private long _dragStillSince;

    private bool _hasPresented;
    private long _presentedPts;
    private byte[]? _copyScratch;

    /// <summary>Creates a channel for one video track.</summary>
    /// <param name="trackIndex">The index of the track in the cutscene.</param>
    /// <param name="track">The track.</param>
    /// <param name="createSession">Creates a media session. It runs on a background task.</param>
    /// <param name="fail">Receives each failure of a clip. The evaluator reports it once.</param>
    /// <param name="time">The wall clock of the drag dwell.</param>
    public CutsceneVideoChannel(int trackIndex, VideoTrack track, Func<IMediaStreamSession> createSession, Action<CutsceneVideoChannel, ECutsceneVideoFailure, string> fail, TimeProvider time)
    {
        TrackIndex = trackIndex;
        Track = track;
        _createSession = createSession;
        _fail = fail;
        _time = time;
        _dragDwellTimestamps = (long)(DragDwellSeconds * time.TimestampFrequency);
        Cache = new VideoFrameCache(Math.Max(1, track.FrameCacheCapacity), Math.Max(1L, track.FrameCacheBudgetBytes));
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

    /// <summary>The number of frames that wait in the session queue.</summary>
    public int QueuedFrameCount => _session?.QueuedVideoFrameCount ?? 0;

    /// <summary>True while a seek of the channel runs.</summary>
    public bool IsSeekInFlight => _requestInFlight;

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
        if (mode == ECutsceneVideoMode.Keyframe && (Mode != ECutsceneVideoMode.Keyframe || ticks != _dragTicks))
        {
            _dragTicks = ticks;
            _dragStillSince = _time.GetTimestamp();
        }
        DesiredTicks = ticks;
        Mode = mode;
        if (discontinuity)
            _decoderForward = false;
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
        if (!PollRequest(session!))
            return;

        Drain(session!);

        // A drag whose wanted time did not change for the dwell time stopped, so it needs the exact frame.
        ECutsceneVideoMode mode = Mode;
        if (mode == ECutsceneVideoMode.Keyframe && _time.GetTimestamp() - _dragStillSince >= _dragDwellTimestamps)
            mode = ECutsceneVideoMode.Exact;

        long ticks = DesiredTicks;
        if (Cache.TryGetFrame(ticks, out DecodedVideoFrame frame) || (residency is not null && SourcePath is not null && residency.TryCopyFrame(SourcePath, ticks, Cache, ref _copyScratch) && Cache.TryGetFrame(ticks, out frame)))
        {
            Present(frame);
            if (mode == ECutsceneVideoMode.Sequential && NeedsForwardSeek(ticks))
                Request(session!, ticks, ECutsceneVideoMode.Exact);
            return;
        }

        if (mode == ECutsceneVideoMode.Keyframe && _hasKeyframe && _keyframePts <= ticks && ticks <= _keyframeTarget && Cache.TryGetFrameAt(_keyframePts, out frame))
        {
            // The keyframe of an earlier target in this range is also a keyframe at or before this time.
            Present(frame);
            return;
        }

        // The result of the request for this time shows, if the request gives the accuracy that the mode needs.
        if (_hasResult && _requestTicks == ticks && (mode == ECutsceneVideoMode.Keyframe || _requestMode == ECutsceneVideoMode.Exact)
            && Cache.TryGetFrameAt(_resultPts, out frame))
        {
            Present(frame);
            return;
        }

        // During a drag, a finished result of an older time is a frame at or before the wanted time, so it shows while the
        // seek of the new time runs. A keyframe decode that takes longer than a tick then does not freeze the surface.
        if (mode == ECutsceneVideoMode.Keyframe && _hasResult && _resultPts <= ticks && Cache.TryGetFrameAt(_resultPts, out frame))
            Present(frame);

        if (mode == ECutsceneVideoMode.Sequential)
        {
            if (NeedsForwardSeek(ticks))
                Request(session!, ticks, ECutsceneVideoMode.Exact);
            return;
        }
        Request(session!, ticks, mode);
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

    // Reads the state of the seek in flight. A cancelled seek was superseded or closed. A failed seek stops the clip.
    private bool PollRequest(IMediaStreamSession session)
    {
        if (!_requestInFlight)
            return true;

        switch (session.GetSeekState(_requestId))
        {
            case EVideoSeekState.Pending:
                return true;
            case EVideoSeekState.Completed:
                _requestInFlight = false;
                _awaitingResult = true;
                return true;
            case EVideoSeekState.Cancelled:
                _requestInFlight = false;
                _awaitingResult = false;
                return true;
            default:
                _requestInFlight = false;
                Fail();
                _fail(this, ECutsceneVideoFailure.SeekFailed, session.LastSeekError ?? "unknown error");
                return false;
        }
    }

    // Moves the released frames to the cache. The first frame after a completed seek is its result.
    private void Drain(IMediaStreamSession session)
    {
        if (_awaitingResult && session.TryDequeueVideoFrame(ReleaseAnyFrame, out DecodedVideoFrame result))
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
            else if (result.PresentationTimestampTicks + Math.Max(result.DurationTicks, 1L) <= _requestTicks)
            {
                // An exact seek that gives a frame that ends before its target reached the end of the file.
                _fileEndPts = Math.Min(_fileEndPts, result.PresentationTimestampTicks);
            }
        }

        // A keyframe seek pauses the decoder, and the frames of a running seek are not released yet.
        if (_awaitingResult || _requestInFlight || !_decoderForward)
            return;
        while (session.TryDequeueVideoFrame(DesiredTicks, out DecodedVideoFrame frame))
            Accept(frame);
    }

    private void Accept(in DecodedVideoFrame frame)
    {
        Cache.Insert(frame);
        _lastDequeuedPts = frame.PresentationTimestampTicks;
    }

    // Forward playback seeks only when the decoder is not running forward, when the time is before the decoded range, or
    // when the decoder fell far behind. Past the last frame of the file, no seek helps.
    private bool NeedsForwardSeek(long ticks)
    {
        if (_requestInFlight || _awaitingResult)
            return false;
        if (!_decoderForward)
            return true;
        if (ticks < _forwardBaseTicks)
            return true;
        if (_lastDequeuedPts >= _fileEndPts)
            return false;
        return _lastDequeuedPts != long.MinValue && ticks > _lastDequeuedPts + MaxDecoderLagTicks;
    }

    private void Request(IMediaStreamSession session, long ticks, ECutsceneVideoMode mode)
    {
        // One seek at a time. The newest time is asked for when the seek in flight ends.
        if (_requestInFlight)
            return;
        if (_requestTicks == ticks && _requestMode == mode && (_awaitingResult || _hasResult) && _requestId >= 0)
            return;

        int id = session.RequestSeek(ticks, mode == ECutsceneVideoMode.Keyframe ? EVideoSeekMode.NearestKeyframe : EVideoSeekMode.Exact);
        if (id < 0)
        {
            Fail();
            _fail(this, ECutsceneVideoFailure.SeekFailed, "the session refused the seek");
            return;
        }

        _requestId = id;
        _requestInFlight = true;
        _requestTicks = ticks;
        _requestMode = mode;
        _awaitingResult = false;
        _hasResult = false;
        _decoderForward = mode == ECutsceneVideoMode.Exact;
        _forwardBaseTicks = ticks;
        _lastDequeuedPts = long.MinValue;
        SeekRequestCount++;
    }

    private void Present(in DecodedVideoFrame frame)
    {
        if (Target is not { } target)
            return;
        if (_hasPresented && _presentedPts == frame.PresentationTimestampTicks)
            return;

        // A forward drag never steps back: a frame older than the shown frame does not show while the shown frame is still
        // at or before the wanted time. A backward drag (the wanted time is before the shown frame) shows older keyframes.
        if (Mode == ECutsceneVideoMode.Keyframe && _hasPresented && _presentedPts <= DesiredTicks && frame.PresentationTimestampTicks < _presentedPts)
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
        _requestId = -1;
        _requestInFlight = false;
        _awaitingResult = false;
        _hasResult = false;
        _hasKeyframe = false;
        _decoderForward = false;
        _lastDequeuedPts = long.MinValue;
        _fileEndPts = long.MaxValue;
        _dragTicks = long.MinValue;
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
