using XREngine.Audio;
using XREngine.Components;
using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>
/// The streaming state of one audio track. The channel owns the decoder of the active clip and feeds the
/// sources of the bound <see cref="AudioSourceComponent"/> through
/// <see cref="AudioSourceComponent.EnqueueStreamingBuffer(ReadOnlySpan{AudioSource}, int, bool, short[], AudioBuffer[])"/>.
/// The channel keeps its own array of the sources and rebuilds it only when the listeners change, so the frame path does not allocate.
/// Each buffer holds <see cref="AudioTrackEvaluator.ChunkFrames"/> frames. The last buffer of a clip is padded with silence,
/// so every queued buffer has the same length and the play position follows from the number of queued buffers.
/// The channel keeps the last decoded frames in a ring. A restart inside that ring, for example a resume after a pause,
/// reads from the ring and does not seek the decoder backward.
/// </summary>
/// <remarks>
/// <para>
/// A clip with an audio proxy uses two tiers. A restart outside the ring does not seek on the update thread: the channel
/// fills the queue from the proxy (T0) at the exact frame in the same frame, and an <see cref="AudioDecoderSeekWorker"/>
/// seeks the full decoder to a later buffer boundary in the background. At the first buffer boundary at which the full
/// decoder is ready at the produced frame, the channel crossfades to it over 20 ms (<see cref="AudioTierCrossfader"/>).
/// The output keeps the rate and the channel count of the full decoder, so the queue, the ring, and the clock frames
/// do not change at the tier change. A stale proxy gives silence in place of the proxy, with a fade-in at the change.
/// A clip with no proxy seeks on the update thread, as before.
/// </para>
/// <para>
/// While the playhead moves in a scrub, <see cref="Scrub"/> plays short grains from the proxy (<see cref="AudioScrubGrainPlayer"/>).
/// </para>
/// </remarks>
internal sealed class CutsceneAudioChannel
{
    private const int MaxChannels = 2;
    private const int QueueTarget = 4;
    private const int ChunkFrames = AudioTrackEvaluator.ChunkFrames;

    /// <summary>
    /// The number of buffers after a restart at which the full decoder takes over first. The restart fills the queue with
    /// <see cref="QueueTarget"/> buffers, so the first buffer that a later refill adds is the first possible change.
    /// </summary>
    private const int InitialTierLeadChunks = QueueTarget;

    /// <summary>The largest lead after seeks that came too late, in buffers.</summary>
    private const int MaxTierLeadChunks = 256;

    /// <summary>The ring holds the queue and two more buffers, so a restart near the heard frame is inside it.</summary>
    private const int HistoryFrames = (QueueTarget + 2) * ChunkFrames;

    private readonly float[] _decoded = new float[ChunkFrames * MaxChannels];
    private readonly short[] _mono = new short[ChunkFrames];
    private readonly short[] _stereo = new short[ChunkFrames * MaxChannels];
    private readonly AudioBuffer[] _batch = new AudioBuffer[1];
    private readonly float[] _history = new float[HistoryFrames * MaxChannels];
    private readonly TimeProvider _time;

    private AudioSourceComponent? _component;
    private bool _savedExternalBufferManagement;
    private AudioSource[] _sources = [];
    private bool[] _savedAutoPlay = [];
    private int _sourceCount;
    private int _listenerVersion;

    // The sources at index 0 to _feedCount - 1 get the stream. A source after the first that stopped again after its own
    // restart moves to the end of the list, after _feedCount, and gets no buffers until the next full restart.
    private int _feedCount;

    // For each source after the first: true after the restart of that source alone, until the source plays a buffer.
    private bool[] _sourceRestarted = [];
    private readonly Action<CutsceneAudioChannel, ListenerContext> _sourceLost;
    private readonly Action<CutsceneAudioChannel, Exception> _seekFailed;
    private readonly TaskScheduler _seekScheduler;
    private readonly AudioTierCrossfader _crossfader = new();
    private readonly AudioScrubGrainPlayer _grains = new();

    private IStreamingAudioDecoder? _decoder;
    private int _sampleRate;
    private int _channels;
    private long _totalFrames;

    // The seek worker of a clip with two tiers. It owns the decoder, and the channel uses the decoder only while it is idle.
    private AudioDecoderSeekWorker? _seeker;

    // True while the produced frames come from the proxy, or from silence for a stale proxy, and not from the full decoder.
    private bool _waitingForFull;
    private bool _fadeFromSilence;
    private int _crossfadeDone = int.MaxValue;
    private int _tierLeadChunks = InitialTierLeadChunks;
    private bool _seekFailureReported;

    // The frame of the last service or scrub, a grain request that waits for the end of the grain before it, and a playing grain.
    private long _lastFrame = -1;
    private bool _grainPending;
    private bool _grainActive;

    private long _endFrame;
    private long _producedFrame;
    private long _historyCount;
    private long _readFrame;
    private long _nextFrame;
    private long _restartFrame;
    private bool _endOfData;
    private bool _streaming;
    private bool _needsRestart;
    private float _appliedGain = float.NaN;

    // The wall time of the play steps in the current warm-up, and the output latency of the last clock reading.
    private double _warmUpSeconds;
    private long _lastLatencyNanoseconds;

    // True from a restart to the first valid clock reading after it. That reading moves the clock to the heard position in one step.
    private bool _stepPending;

    // True after a restart because a source ran out of buffers, until the next valid clock reading. A second starved restart
    // keeps the warm-up time, so a source that never plays reaches the warm-up bound.
    private bool _starvedSinceLastRead;

    /// <summary>Creates the channel of a track.</summary>
    /// <param name="trackIndex">The index of the track in the cutscene asset.</param>
    /// <param name="track">The track.</param>
    /// <param name="time">The wall clock that measures the time of each restart.</param>
    /// <param name="sourceLost">
    /// Called once when a source after the first stops again after its own restart. The channel then gives that source no buffers.
    /// </param>
    /// <param name="seekFailed">
    /// Called once for each clip when a background seek of the full decoder fails. The clip then plays the proxy to its end.
    /// </param>
    /// <param name="seekScheduler">The scheduler of the background seeks of the full decoder.</param>
    public CutsceneAudioChannel(
        int trackIndex,
        AudioTrack track,
        TimeProvider time,
        Action<CutsceneAudioChannel, ListenerContext> sourceLost,
        Action<CutsceneAudioChannel, Exception> seekFailed,
        TaskScheduler seekScheduler)
    {
        TrackIndex = trackIndex;
        Track = track;
        _time = time;
        _sourceLost = sourceLost;
        _seekFailed = seekFailed;
        _seekScheduler = seekScheduler;
    }

    /// <summary>True while the produced frames come from the proxy (or from silence for a stale proxy) and not from the full decoder.</summary>
    public bool IsWaitingForFullDecoder => _waitingForFull;

    /// <summary>True if the open clip uses two tiers: a proxy, or silence for a stale proxy, until the full decoder is ready.</summary>
    public bool UsesTiers => _seeker is not null;

    /// <summary>The number of background seeks of the full decoder of the open clip that started.</summary>
    public int BackgroundSeekCount => _seeker?.SeekCount ?? 0;

    /// <summary>The index of the track in the cutscene asset.</summary>
    public int TrackIndex { get; }

    /// <summary>The track that this channel plays.</summary>
    public AudioTrack Track { get; }

    /// <summary>The index of the clip that has an open decoder, or -1.</summary>
    public int ClipIndex { get; private set; } = -1;

    /// <summary>The sample rate of the open decoder.</summary>
    public int SampleRate => _decoder is null ? 0 : _sampleRate;

    /// <summary>The bound component, or null.</summary>
    public AudioSourceComponent? Component => _component;

    /// <summary>True while the sources play the stream.</summary>
    public bool IsStreaming => _streaming;

    /// <summary>The number of sources that the channel feeds.</summary>
    public int SourceCount => _sourceCount;

    /// <summary>The output latency in nanoseconds that the transport reported at the last clock reading.</summary>
    public long LastLatencyNanoseconds => _lastLatencyNanoseconds;

    /// <summary>
    /// Clears the step of the first clock reading after a restart. Use it when the channel starts to correct the clock
    /// with no restart, so that its first reading slews and does not step.
    /// </summary>
    public void ClearPendingStep()
        => _stepPending = false;

    /// <summary>
    /// Takes control of the buffers of a component. A different component releases the earlier one first.
    /// While the channel holds a component, the component does not unqueue buffers itself and its sources do not play on a queue.
    /// </summary>
    public void Bind(AudioSourceComponent component)
    {
        if (ReferenceEquals(component, _component))
            return;

        Release();
        _component = component;
        _savedExternalBufferManagement = component.ExternalBufferManagement;
        component.ExternalBufferManagement = true;
        _sourceCount = -1;
    }

    /// <summary>Opens the decoder of a clip. The previous decoder closes, and the stream restarts at the next service.</summary>
    /// <param name="clipIndex">The index of the clip.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="decoder">The decoder of the clip file. The channel owns it from now on.</param>
    /// <param name="proxy">The decoder of the audio proxy of the file, or null. The channel owns it from now on.</param>
    /// <param name="useTiers">
    /// True to seek the full decoder in the background after a restart outside the ring. Until it is ready, the channel plays
    /// <paramref name="proxy"/>, or silence if it is null (a stale proxy). False seeks on the update thread.
    /// </param>
    public void Open(int clipIndex, AudioTrackClip clip, IStreamingAudioDecoder decoder, ImaAdpcmDecoder? proxy = null, bool useTiers = false)
    {
        CloseDecoder();
        _decoder = decoder;
        _sampleRate = decoder.SampleRate;
        _channels = decoder.Channels;
        _totalFrames = decoder.TotalFrames;
        ClipIndex = clipIndex;
        long clipEnd = (clip.ClipOffset + clip.Duration).ToSamples(_sampleRate);
        _endFrame = Math.Clamp(clipEnd, 0L, _totalFrames);
        _producedFrame = 0;
        _historyCount = 0;
        _readFrame = 0;
        _lastFrame = -1;
        _grainPending = false;
        _seekFailureReported = false;

        // A new decoder is at frame zero, so the full tier is ready there with no crossfade.
        _waitingForFull = false;
        _crossfadeDone = int.MaxValue;
        if (!useTiers)
        {
            proxy?.Dispose();
            return;
        }

        _seeker = new AudioDecoderSeekWorker(decoder, _seekScheduler);
        _grains.Configure(_sampleRate, _channels);
        _crossfader.Open(proxy, _sampleRate, _channels, Math.Max(ChunkFrames, _grains.GrainFrames));
    }

    /// <summary>
    /// Keeps the sources at the clip frame. A restart stops the sources, moves the read position, fills the queue, and plays again.
    /// Without a restart, the channel unqueues played buffers and adds new ones.
    /// </summary>
    /// <param name="targetFrame">The frame in the file at the cutscene time.</param>
    /// <param name="restart">True to restart even if the stream plays, for example after a jump.</param>
    /// <param name="gain">The gain to set on each source.</param>
    /// <param name="restartSeconds">
    /// The wall time in seconds that the restart took, or zero. The sources start only at the end of the restart.
    /// </param>
    /// <returns>True if the stream restarted.</returns>
    public bool Service(long targetFrame, bool restart, float gain, out double restartSeconds)
    {
        restartSeconds = 0.0;
        if (_component is null || _decoder is null)
            return false;
        _lastFrame = targetFrame;
        _grainPending = false;

        // A new listener gets buffers only from now on, so all sources restart together to stay aligned.
        if (RefreshSources())
            restart = true;
        if (_sourceCount == 0)
        {
            _streaming = false;
            return false;
        }

        // A first source that ran out of buffers, for example after a long frame, stops. OpenAL then counts each buffer that is
        // queued on it as played at once, so a refill cannot fill the queue. A restart at the cutscene time rewinds the
        // sources and keeps the sound aligned with the picture. The clock reads the first source, so only its state starts a full restart.
        bool starved = _streaming && !restart && !_needsRestart && IsStarved();
        bool restarted = !_streaming || restart || _needsRestart || starved;
        if (restarted)
        {
            // A restart because the queues differ keeps the warm-up time, so a mismatch that comes back each time reaches the bound.
            // A second starved restart with no valid reading between them keeps it too, so a source that never plays reaches the bound.
            // The first starved restart, for example after one long frame, starts a new warm-up.
            bool repeatedStarve = starved && _starvedSinceLastRead;
            bool keepWarmUp = _needsRestart || repeatedStarve;

            // Only a starved restart marks the next starve as repeated. A seek or a resume starts a new count.
            _starvedSinceLastRead = starved;
            long start = _time.GetTimestamp();
            Restart(targetFrame, keepWarmUp);

            // A repeated starved restart plays no sound that the clock follows, so its time is not removed from the clock.
            if (!repeatedStarve)
                restartSeconds = _time.GetElapsedTime(start).TotalSeconds;
        }
        else
        {
            Pump();
            ServiceOtherSources();
        }
        ApplyGain(gain);
        return restarted;
    }

    /// <summary>
    /// Reads the frame of the clip file that the listener hears now, from the clock of the first source.
    /// The value is the number of frames before the queue, plus the source position, minus the output latency.
    /// </summary>
    /// <param name="deltaSeconds">The wall time of this play step. It counts toward the warm-up bound.</param>
    /// <param name="frame">The heard frame in the file.</param>
    /// <param name="warmingUp">
    /// True if the position has no valid value yet, but this is expected: the listener does not hear the queue of the last
    /// restart yet (the value is below the restart frame), or the sources wait for a restart. A restart is necessary when
    /// the queues differ, when the first source ran out of buffers, and when the listeners changed.
    /// A warm-up that lasts longer than <see cref="WarmUpBoundSeconds"/> is a lost clock, so the value is then false.
    /// The value is also true after the sources played all the data of the clip. This time does not count toward the bound.
    /// False also if the transport cannot report a clock.
    /// </param>
    /// <param name="firstAfterRestart">True if this is the first valid frame after the last restart.</param>
    /// <returns>True if the frame is valid.</returns>
    public bool TryReadHeardFrame(double deltaSeconds, out long frame, out bool warmingUp, out bool firstAfterRestart)
    {
        frame = 0;
        warmingUp = false;
        firstAfterRestart = false;
        if (_sourceCount <= 0 || _decoder is null || _component is not { } component)
            return false;

        // After a listener change, a source in the list can be back in its listener pool, so the channel must not read it.
        // The next service rebuilds the list and restarts all sources.
        if (component.ActiveListenerCount != _sourceCount || component.ListenerVersion != _listenerVersion)
            return WarmUp(deltaSeconds, out warmingUp);

        // Queues that differ give a wrong queue start until the restart of the next service.
        if (_needsRestart)
            return WarmUp(deltaSeconds, out warmingUp);

        // A first source that ran out of buffers stopped at the end of its queue, so its position is old. The next service restarts it.
        // The first starve after a valid reading, for example after one long frame, is an expected warm-up, so its step does not
        // count toward the bound. A starve that comes back with no valid reading between counts.
        if (IsStarved())
        {
            if (_starvedSinceLastRead)
                return WarmUp(deltaSeconds, out warmingUp);
            warmingUp = true;
            return false;
        }

        // After the last buffer of the clip, the stopped source does not move, but the listener still hears the output latency.
        // The heard position then stays behind the clock, so the clock does not read it.
        AudioSource primary = _sources[0];
        if (_endOfData && !primary.IsPlaying)
        {
            warmingUp = true;
            return false;
        }

        if (!primary.TryGetPlaybackClock(out long sampleFrame, out long latencyNanoseconds))
            return false;
        _lastLatencyNanoseconds = latencyNanoseconds;

        long queueStart = _nextFrame - (long)primary.BuffersQueued * ChunkFrames;
        long latencyFrames = (long)((Int128)latencyNanoseconds * _sampleRate / 1_000_000_000L);
        long heard = queueStart + sampleFrame - latencyFrames;

        // No frame before the restart frame was queued, so the device still plays older output. The clock must not follow it.
        if (heard < _restartFrame)
            return WarmUp(deltaSeconds, out warmingUp);

        _warmUpSeconds = 0.0;
        _starvedSinceLastRead = false;
        frame = heard;
        firstAfterRestart = _stepPending;
        _stepPending = false;
        return true;
    }

    /// <summary>
    /// The longest expected warm-up after a restart, in seconds of wall time: the output latency plus 250 ms, and at least 500 ms.
    /// </summary>
    public double WarmUpBoundSeconds
        => Math.Max(_lastLatencyNanoseconds / 1e9 + 0.25, 0.5);

    // Counts the step toward the warm-up. Past the bound, the device does not move or the queues never agree, so the clock is lost.
    private bool WarmUp(double deltaSeconds, out bool warmingUp)
    {
        _warmUpSeconds += Math.Max(0.0, deltaSeconds);
        warmingUp = _warmUpSeconds <= WarmUpBoundSeconds;
        return false;
    }

    /// <summary>
    /// Stops the sources and drops the queue, but keeps the decoder and the ring of decoded frames.
    /// The next service restarts the stream, and a restart inside the ring needs no decoder seek.
    /// </summary>
    public void Silence()
    {
        if (_component is null)
            return;

        // A listener change since the last service must not leave a removed source in the list.
        if (RefreshSources())
            _appliedGain = float.NaN;
        if (_streaming || _grainActive)
            StopSources();
        _streaming = false;
        _grainActive = false;
        _grainPending = false;
        ApplyGain(0.0f);
    }

    /// <summary>
    /// Stops the sources as <see cref="Silence()"/> does, and records the frame, so a later scrub to the same frame plays no grain.
    /// </summary>
    /// <param name="frame">The frame in the file at the playhead.</param>
    public void Silence(long frame)
    {
        _lastFrame = frame;
        Silence();
    }

    /// <summary>
    /// Stops the stream and plays scrub grains from the proxy. When the frame differs from the frame of the last service
    /// or scrub, the channel plays one grain at the frame. It waits for the end of a playing grain, and then plays the newest
    /// frame. A frame that does not move plays nothing more. A clip with no proxy stays silent.
    /// </summary>
    /// <param name="targetFrame">The frame in the file at the playhead.</param>
    /// <param name="gain">The gain to set on each source.</param>
    public void Scrub(long targetFrame, float gain)
    {
        if (_component is null)
            return;

        if (RefreshSources())
            _appliedGain = float.NaN;
        if (_streaming)
        {
            StopSources();
            _streaming = false;
        }

        bool moved = targetFrame != _lastFrame;
        _lastFrame = targetFrame;
        if (_decoder is null || !_crossfader.HasProxy || _sourceCount == 0 || targetFrame < 0 || targetFrame >= _endFrame)
        {
            if (_grainActive)
                StopSources();
            _grainActive = false;
            _grainPending = false;
            ApplyGain(0.0f);
            return;
        }

        if (moved)
            _grainPending = true;
        if (_grainPending && !(_grainActive && _sources[0].IsPlaying))
        {
            _grainPending = false;
            PlayGrain(targetFrame);
        }
        ApplyGain(gain);
    }

    // Queues one grain on each source and plays it. The grain buffer is the same array each time, so this does not allocate.
    private void PlayGrain(long frame)
    {
        StopSources();
        short[] pcm = _grains.Build(_crossfader, frame);
        _component!.EnqueueStreamingBuffer(_sources.AsSpan(0, _sourceCount), _sampleRate, _channels == 2, pcm, _batch);
        for (int i = 0; i < _sourceCount; i++)
            if (_sources[i].BuffersQueued > 0)
                _sources[i].Play();
        _grainActive = true;
    }

    /// <summary>
    /// Stops the sources, closes the decoder, and gives the component back with its earlier settings:
    /// buffer management, the play-on-queue flag of each source, and the source gain.
    /// </summary>
    public void Release()
    {
        bool streaming = _streaming || _grainActive;
        _grainActive = false;
        _grainPending = false;
        CloseDecoder();
        AudioSourceComponent? component = _component;
        if (component is null)
            return;

        RefreshSources();
        if (streaming)
            StopSources();
        RestoreSources();
        component.ExternalBufferManagement = _savedExternalBufferManagement;
        _component = null;
        _sourceCount = 0;
        _appliedGain = float.NaN;
    }

    /// <summary>Stops the stream and closes the decoder. The component stays bound.</summary>
    public void Deactivate()
    {
        Silence();
        CloseDecoder();
    }

    private void CloseDecoder()
    {
        // A running background seek uses the decoder, so the worker closes it at the end of that seek.
        if (_seeker is not null)
            _seeker.Close();
        else
            _decoder?.Dispose();
        _seeker = null;
        _crossfader.Close();
        _decoder = null;
        ClipIndex = -1;
        _streaming = false;
        _endOfData = false;
        _needsRestart = false;
        _historyCount = 0;
        _waitingForFull = false;
        _crossfadeDone = int.MaxValue;
    }

    // Rebuilds the source list when the listeners change: a new count or a new listener version, which also catches a swap
    // at the same count. The enumeration of the listener table allocates, so it runs only on a change and not in each frame.
    private bool RefreshSources()
    {
        AudioSourceComponent component = _component!;
        int count = component.ActiveListenerCount;
        int version = component.ListenerVersion;
        if (count == _sourceCount && version == _listenerVersion)
            return false;
        _listenerVersion = version;

        RestoreSources();
        if (_sources.Length < count)
        {
            _sources = new AudioSource[count];
            _savedAutoPlay = new bool[count];
            _sourceRestarted = new bool[count];
        }
        Array.Clear(_sourceRestarted);
        int index = 0;
        foreach (KeyValuePair<ListenerContext, AudioSource> pair in component.ActiveListeners)
        {
            if (index >= _sources.Length)
                break;
            AudioSource source = pair.Value;
            _savedAutoPlay[index] = source.AutoPlayOnQueue;
            source.AutoPlayOnQueue = false;
            _sources[index++] = source;
        }
        _sourceCount = index;
        _feedCount = index;
        _appliedGain = float.NaN;
        return true;
    }

    // Puts back the play-on-queue flag and the gain of each source in the list, then clears the list.
    // A source that left the listener table went back to the listener pool, and another owner can use it now,
    // so the method writes nothing to it.
    private void RestoreSources()
    {
        AudioSourceComponent? component = _component;
        float gain = component?.Gain ?? 1.0f;
        for (int i = 0; i < _sourceCount; i++)
        {
            AudioSource source = _sources[i];
            if (component is null || !IsInTable(component, source))
                continue;
            source.AutoPlayOnQueue = _savedAutoPlay[i];
            source.Gain = gain;
        }
        Array.Clear(_sources);
        _sourceCount = 0;
        _feedCount = 0;
    }

    private static bool IsInTable(AudioSourceComponent component, AudioSource source)
        => component.ActiveListeners.TryGetValue(source.ParentListener, out AudioSource? current) && ReferenceEquals(current, source);

    private void Restart(long targetFrame, bool keepWarmUp)
    {
        if (!keepWarmUp)
            _warmUpSeconds = 0.0;
        StopSources();
        _grainActive = false;

        long frame = Math.Clamp(targetFrame, 0L, _endFrame);
        long historyStart = _producedFrame - _historyCount;
        if (frame < historyStart || frame > _producedFrame)
        {
            if (_seeker is not null)
                BeginTierChange(frame);
            else
                _decoder!.SeekToFrame(frame);
            _producedFrame = frame;
            _historyCount = 0;
        }

        _readFrame = frame;
        _nextFrame = frame;
        _restartFrame = frame;
        _endOfData = frame >= _endFrame;
        _needsRestart = false;

        // A full restart gives every source the stream again, also a source that the channel gave up.
        _feedCount = _sourceCount;
        Array.Clear(_sourceRestarted, 0, _sourceCount);
        Fill();

        _streaming = true;
        _stepPending = true;
        if (_sources[0].BuffersQueued > 0)
            PlaySources();
    }

    // True if the first source stopped before the end of the data, so its queue ran out. The clock reads the first source,
    // so only its state starts a full restart. ServiceOtherSources handles the other sources.
    private bool IsStarved()
        => !_endOfData && !_sources[0].IsPlaying;

    // A source after the first that stopped before the end of the data restarts alone, once. It gets another restart only after it
    // played a buffer (see Pump), or after a full restart. This does not depend on a clock reading, so it also works on a track that
    // does not drive the clock and on a transport with no playback clock. If it stops again, the channel reports it once and gives it no
    // more buffers, so it cannot make the first source restart. The loop is over the cached list, so it does not allocate.
    private void ServiceOtherSources()
    {
        if (_endOfData)
            return;

        for (int i = 1; i < _feedCount; i++)
        {
            AudioSource source = _sources[i];
            if (source.IsPlaying)
                continue;

            if (_sourceRestarted[i])
            {
                StopSource(source);
                _sourceLost(this, source.ParentListener);
                RemoveFromFeed(i);
                i--;
                continue;
            }

            _sourceRestarted[i] = true;
            RestartOneSource(i);
        }
    }

    // Queues the frames of the queue of the first source again on one source and plays it. The source starts at the buffer that the
    // first source plays now, so it can be up to one buffer behind the first source.
    private void RestartOneSource(int index)
    {
        AudioSource source = _sources[index];
        StopSource(source);

        AudioSource primary = _sources[0];
        long queueStart = _nextFrame - (long)primary.BuffersQueued * ChunkFrames;
        long frame = queueStart + (long)Math.Min(primary.BuffersProcessed, primary.BuffersQueued) * ChunkFrames;
        long historyStart = _producedFrame - _historyCount;
        if (frame < historyStart || _nextFrame > _producedFrame)
            return;

        int channels = _channels;
        bool stereo = channels == 2;
        short[] pcm = stereo ? _stereo : _mono;
        for (; frame < _nextFrame; frame += ChunkFrames)
        {
            CopyHistory(frame, ChunkFrames, channels);
            int samples = ChunkFrames * channels;
            for (int i = 0; i < samples; i++)
                pcm[i] = ToShort(_decoded[i]);
            if (_component!.EnqueueStreamingBuffer(_sources.AsSpan(index, 1), _sampleRate, stereo, pcm, _batch) < 1)
                break;
        }
        if (source.BuffersQueued > 0)
            source.Play();
    }

    // Moves a source to the end of the list, after the sources that get the stream. Its saved settings move with it.
    private void RemoveFromFeed(int index)
    {
        int last = --_feedCount;
        (_sources[index], _sources[last]) = (_sources[last], _sources[index]);
        (_savedAutoPlay[index], _savedAutoPlay[last]) = (_savedAutoPlay[last], _savedAutoPlay[index]);
        (_sourceRestarted[index], _sourceRestarted[last]) = (_sourceRestarted[last], _sourceRestarted[index]);
    }

    private void CopyHistory(long frame, int frames, int channels)
    {
        int done = 0;
        while (done < frames)
        {
            int ring = (int)((frame + done) % HistoryFrames);
            int count = Math.Min(frames - done, HistoryFrames - ring);
            _history.AsSpan(ring * channels, count * channels).CopyTo(_decoded.AsSpan(done * channels));
            done += count;
        }
    }

    private void Pump()
    {
        for (int i = 0; i < _feedCount; i++)
        {
            AudioSource source = _sources[i];
            if (source.BuffersProcessed <= 0)
                continue;

            // A played buffer shows that a source after a restart of its own works again, so a later stop gets a new restart.
            if (i > 0 && source.IsPlaying)
                _sourceRestarted[i] = false;
            source.UnqueueConsumedBuffers();
        }

        Fill();

        // After the last buffer of the data, a source that played its queue stops. Start it again if new buffers are in its queue.
        if (!_endOfData)
            return;
        for (int i = 0; i < _feedCount; i++)
        {
            AudioSource source = _sources[i];
            if (!source.IsPlaying && source.BuffersQueued > 0)
                source.Play();
        }
    }

    private void Fill()
    {
        AudioSourceComponent component = _component!;
        int channels = _channels;
        int target = Math.Min(QueueTarget, Math.Max(1, component.MaxStreamingBuffers));
        bool stereo = channels == 2;
        short[] pcm = stereo ? _stereo : _mono;

        // The loop adds at most the target count of buffers. A source that does not keep its queued buffers, for example a
        // stopped OpenAL source, then cannot make one call decode the rest of the file.
        for (int added = 0; added < target && !_endOfData && _sources[0].BuffersQueued < target; added++)
        {
            int wanted = (int)Math.Min(ChunkFrames, _endFrame - _readFrame);
            int frames = wanted > 0 ? ReadFrames(wanted) : 0;
            if (frames <= 0)
            {
                _endOfData = true;
                break;
            }

            int samples = frames * channels;
            for (int i = 0; i < samples; i++)
                pcm[i] = ToShort(_decoded[i]);
            // The padding keeps every buffer at the same length, so the clock can count buffers.
            Array.Clear(pcm, samples, pcm.Length - samples);

            int accepted = component.EnqueueStreamingBuffer(_sources.AsSpan(0, _feedCount), _sampleRate, stereo, pcm, _batch);
            if (accepted < _feedCount)
            {
                // The frames stay in the ring, so the read position goes back and a later fill offers them again.
                _readFrame -= frames;

                // Some sources took the buffer and some did not, so their queues differ. All of them restart at the next service.
                if (accepted > 0)
                    _needsRestart = true;
                break;
            }

            _nextFrame += ChunkFrames;

            // A short read means the end of the clip or of the file, so the read position and the frame count stay equal.
            if (frames < ChunkFrames || _nextFrame >= _endFrame)
                _endOfData = true;
        }
    }

    // Reads frames at the read position into the decode buffer. Frames before the produced frame come from the ring.
    // New frames come from the full decoder, or from the proxy while the full decoder is not ready, and go into the ring too.
    private int ReadFrames(int frames)
    {
        int channels = _channels;
        int got = 0;
        while (got < frames)
        {
            if (_readFrame < _producedFrame)
            {
                int count = (int)Math.Min(_producedFrame - _readFrame, frames - got);
                int ring = (int)(_readFrame % HistoryFrames);
                count = Math.Min(count, HistoryFrames - ring);
                _history.AsSpan(ring * channels, count * channels).CopyTo(_decoded.AsSpan(got * channels));
                got += count;
                _readFrame += count;
                continue;
            }

            int want = frames - got;
            Span<float> destination = _decoded.AsSpan(got * channels, want * channels);
            int read = Produce(destination, want, atBufferStart: got == 0);
            if (read <= 0)
                break;

            StoreHistory(destination[..(read * channels)], channels);
            got += read;
            _readFrame += read;
            if (read < want)
                break;
        }
        return got;
    }

    // Makes new frames at the produced frame. The full decoder takes over only at the start of a buffer.
    private int Produce(Span<float> destination, int frames, bool atBufferStart)
    {
        if (_waitingForFull && atBufferStart)
            TryEnterFullTier();

        if (!_waitingForFull)
        {
            int read = _decoder!.Read(destination);
            if (read > 0 && _crossfadeDone < _crossfader.CrossfadeFrames)
                _crossfader.Crossfade(destination, _producedFrame, read, ref _crossfadeDone, _fadeFromSilence);
            return read;
        }

        // The proxy stands in for the full decoder, so it gives the frames that the full decoder would give.
        int count = (int)Math.Clamp(_totalFrames - _producedFrame, 0L, frames);
        _crossfader.ReadProxy(_producedFrame, destination, count);
        return count;
    }

    // Starts the tier change after a restart outside the ring. The frames come from the proxy at once, and the full decoder
    // seeks in the background to a buffer boundary after the frames that the restart queues.
    private void BeginTierChange(long frame)
    {
        _waitingForFull = true;
        _fadeFromSilence = !_crossfader.HasProxy;
        _crossfadeDone = 0;
        _tierLeadChunks = InitialTierLeadChunks;
        _seeker!.Request(frame + (long)_tierLeadChunks * ChunkFrames);
    }

    // Changes to the full decoder if its seek is complete at the produced frame, which is the start of a buffer.
    // A seek that ended after that frame passed asks again at a later buffer, with twice the lead.
    private void TryEnterFullTier()
    {
        if (!_seeker!.TryGetIdle(out long frame, out Exception? failure))
            return;

        if (failure is not null)
        {
            // The decoder state is not known after a failed seek, so the clip plays the proxy to its end.
            if (!_seekFailureReported)
            {
                _seekFailureReported = true;
                _seekFailed(this, failure);
            }
            return;
        }

        if (frame == _producedFrame)
        {
            _waitingForFull = false;
            return;
        }

        if (frame < _producedFrame)
        {
            _tierLeadChunks = Math.Min(_tierLeadChunks * 2, MaxTierLeadChunks);
            _seeker.Request(_producedFrame + (long)_tierLeadChunks * ChunkFrames);
        }
    }

    private void StoreHistory(ReadOnlySpan<float> samples, int channels)
    {
        int frames = samples.Length / channels;
        int done = 0;
        while (done < frames)
        {
            int ring = (int)((_producedFrame + done) % HistoryFrames);
            int count = Math.Min(frames - done, HistoryFrames - ring);
            samples.Slice(done * channels, count * channels).CopyTo(_history.AsSpan(ring * channels));
            done += count;
        }
        _producedFrame += frames;
        _historyCount = Math.Min(HistoryFrames, _historyCount + frames);
    }

    // The same steps as AudioSourceComponent.StopAndRewindAllSources, on the cached list, so no enumeration allocates.
    private void StopSources()
    {
        for (int i = 0; i < _sourceCount; i++)
            StopSource(_sources[i]);
    }

    private static void StopSource(AudioSource source)
    {
        source.Stop();
        source.UnqueueConsumedBuffers();
        source.Rewind();
    }

    private void PlaySources()
    {
        for (int i = 0; i < _sourceCount; i++)
            if (!_sources[i].IsPlaying)
                _sources[i].Play();
    }

    private void ApplyGain(float gain)
    {
        if (gain == _appliedGain)
            return;
        _appliedGain = gain;
        for (int i = 0; i < _sourceCount; i++)
            _sources[i].Gain = gain;
    }

    // The scale is the inverse of the 16-bit decode, so 16-bit source data passes through unchanged.
    internal static short ToShort(float sample)
        => (short)Math.Clamp(MathF.Round(sample * 32768.0f), short.MinValue, short.MaxValue);
}
