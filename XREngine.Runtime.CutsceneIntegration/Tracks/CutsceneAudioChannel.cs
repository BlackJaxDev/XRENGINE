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
internal sealed class CutsceneAudioChannel
{
    private const int MaxChannels = 2;
    private const int QueueTarget = 4;
    private const int ChunkFrames = AudioTrackEvaluator.ChunkFrames;

    /// <summary>The ring holds the queue and two more buffers, so a restart near the heard frame is inside it.</summary>
    private const int HistoryFrames = (QueueTarget + 2) * ChunkFrames;

    private readonly float[] _decoded = new float[ChunkFrames * MaxChannels];
    private readonly short[] _mono = new short[ChunkFrames];
    private readonly short[] _stereo = new short[ChunkFrames * MaxChannels];
    private readonly AudioBuffer[] _batch = new AudioBuffer[1];
    private readonly float[] _history = new float[HistoryFrames * MaxChannels];

    private AudioSourceComponent? _component;
    private bool _savedExternalBufferManagement;
    private AudioSource[] _sources = [];
    private bool[] _savedAutoPlay = [];
    private int _sourceCount;
    private int _listenerVersion;

    private IStreamingAudioDecoder? _decoder;
    private long _endFrame;
    private long _decoderFrame;
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

    public CutsceneAudioChannel(int trackIndex, AudioTrack track)
    {
        TrackIndex = trackIndex;
        Track = track;
    }

    /// <summary>The index of the track in the cutscene asset.</summary>
    public int TrackIndex { get; }

    /// <summary>The track that this channel plays.</summary>
    public AudioTrack Track { get; }

    /// <summary>The index of the clip that has an open decoder, or -1.</summary>
    public int ClipIndex { get; private set; } = -1;

    /// <summary>The sample rate of the open decoder.</summary>
    public int SampleRate => _decoder?.SampleRate ?? 0;

    /// <summary>The bound component, or null.</summary>
    public AudioSourceComponent? Component => _component;

    /// <summary>True while the sources play the stream.</summary>
    public bool IsStreaming => _streaming;

    /// <summary>The number of sources that the channel feeds.</summary>
    public int SourceCount => _sourceCount;

    /// <summary>The output latency in nanoseconds that the transport reported at the last clock reading.</summary>
    public long LastLatencyNanoseconds => _lastLatencyNanoseconds;

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
    public void Open(int clipIndex, AudioTrackClip clip, IStreamingAudioDecoder decoder)
    {
        CloseDecoder();
        _decoder = decoder;
        ClipIndex = clipIndex;
        long clipEnd = (clip.ClipOffset + clip.Duration).ToSamples(decoder.SampleRate);
        _endFrame = Math.Clamp(clipEnd, 0L, decoder.TotalFrames);
        _decoderFrame = 0;
        _historyCount = 0;
        _readFrame = 0;
    }

    /// <summary>
    /// Keeps the sources at the clip frame. A restart stops the sources, moves the read position, fills the queue, and plays again.
    /// Without a restart, the channel unqueues played buffers and adds new ones.
    /// </summary>
    /// <param name="targetFrame">The frame in the file at the cutscene time.</param>
    /// <param name="restart">True to restart even if the stream plays, for example after a jump.</param>
    /// <param name="gain">The gain to set on each source.</param>
    public void Service(long targetFrame, bool restart, float gain)
    {
        if (_component is null || _decoder is null)
            return;

        // A new listener gets buffers only from now on, so all sources restart together to stay aligned.
        if (RefreshSources())
            restart = true;
        if (_sourceCount == 0)
        {
            _streaming = false;
            return;
        }

        // A source that ran out of buffers, for example after a long frame, stops. OpenAL then counts each buffer that is
        // queued on it as played at once, so a refill cannot fill the queue. A restart at the cutscene time rewinds the
        // sources and keeps the sound aligned with the picture.
        if (!_streaming || restart || _needsRestart || IsStarved())
            Restart(targetFrame);
        else
            Pump();
        ApplyGain(gain);
    }

    /// <summary>
    /// Reads the frame of the clip file that the listener hears now, from the clock of the first source.
    /// The value is the number of frames before the queue, plus the source position, minus the output latency.
    /// </summary>
    /// <param name="deltaSeconds">The wall time of this play step. It counts toward the warm-up bound.</param>
    /// <param name="frame">The heard frame in the file.</param>
    /// <param name="warmingUp">
    /// True if the position has no valid value yet, but this is expected: the listener does not hear the queue of the last
    /// restart yet (the value is below the restart frame), or the sources wait for a restart because their queues differ
    /// or because the first source ran out of buffers.
    /// A warm-up that lasts longer than <see cref="WarmUpBoundSeconds"/> is a lost clock, so the value is then false.
    /// False also if the transport cannot report a clock.
    /// </param>
    /// <returns>True if the frame is valid.</returns>
    public bool TryReadHeardFrame(double deltaSeconds, out long frame, out bool warmingUp)
    {
        frame = 0;
        warmingUp = false;
        if (_sourceCount <= 0 || _decoder is null)
            return false;

        // Queues that differ give a wrong queue start until the restart of the next service. A source that ran out of
        // buffers stopped at the end of its queue, so its position is old too. The next service restarts both.
        if (_needsRestart || IsStarved())
            return WarmUp(deltaSeconds, out warmingUp);

        AudioSource primary = _sources[0];
        if (!primary.TryGetPlaybackClock(out long sampleFrame, out long latencyNanoseconds))
            return false;
        _lastLatencyNanoseconds = latencyNanoseconds;

        long queueStart = _nextFrame - (long)primary.BuffersQueued * ChunkFrames;
        long latencyFrames = (long)((Int128)latencyNanoseconds * _decoder.SampleRate / 1_000_000_000L);
        long heard = queueStart + sampleFrame - latencyFrames;

        // No frame before the restart frame was queued, so the device still plays older output. The clock must not follow it.
        if (heard < _restartFrame)
            return WarmUp(deltaSeconds, out warmingUp);

        _warmUpSeconds = 0.0;
        frame = heard;
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
        if (_streaming)
            StopSources();
        _streaming = false;
        ApplyGain(0.0f);
    }

    /// <summary>
    /// Stops the sources, closes the decoder, and gives the component back with its earlier settings:
    /// buffer management, the play-on-queue flag of each source, and the source gain.
    /// </summary>
    public void Release()
    {
        bool streaming = _streaming;
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
        _decoder?.Dispose();
        _decoder = null;
        ClipIndex = -1;
        _streaming = false;
        _endOfData = false;
        _needsRestart = false;
        _historyCount = 0;
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
        }
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
    }

    private static bool IsInTable(AudioSourceComponent component, AudioSource source)
        => component.ActiveListeners.TryGetValue(source.ParentListener, out AudioSource? current) && ReferenceEquals(current, source);

    private void Restart(long targetFrame)
    {
        IStreamingAudioDecoder decoder = _decoder!;

        // A restart because the queues differ keeps the warm-up time, so a mismatch that comes back each time reaches the bound.
        if (!_needsRestart)
            _warmUpSeconds = 0.0;
        StopSources();

        long frame = Math.Clamp(targetFrame, 0L, _endFrame);
        long historyStart = _decoderFrame - _historyCount;
        if (frame < historyStart || frame > _decoderFrame)
        {
            decoder.SeekToFrame(frame);
            _decoderFrame = frame;
            _historyCount = 0;
        }

        _readFrame = frame;
        _nextFrame = frame;
        _restartFrame = frame;
        _endOfData = frame >= _endFrame;
        _needsRestart = false;
        Fill();

        _streaming = true;
        if (_sources[0].BuffersQueued > 0)
            PlaySources();
    }

    // True if the first source stopped before the end of the data, so its queue ran out.
    private bool IsStarved()
        => !_endOfData && !_sources[0].IsPlaying;

    private void Pump()
    {
        for (int i = 0; i < _sourceCount; i++)
        {
            AudioSource source = _sources[i];
            if (source.BuffersProcessed > 0)
                source.UnqueueConsumedBuffers();
        }

        Fill();

        // After the last buffer of the data, a source that played its queue stops. Start it again if new buffers are in its queue.
        AudioSource primary = _sources[0];
        if (!primary.IsPlaying && primary.BuffersQueued > 0)
            PlaySources();
    }

    private void Fill()
    {
        IStreamingAudioDecoder decoder = _decoder!;
        AudioSourceComponent component = _component!;
        int channels = decoder.Channels;
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

            int accepted = component.EnqueueStreamingBuffer(_sources.AsSpan(0, _sourceCount), decoder.SampleRate, stereo, pcm, _batch);
            if (accepted < _sourceCount)
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

    // Reads frames at the read position into the decode buffer. Frames before the decoder position come from the ring.
    // New frames come from the decoder and go into the ring too.
    private int ReadFrames(int frames)
    {
        IStreamingAudioDecoder decoder = _decoder!;
        int channels = decoder.Channels;
        int got = 0;
        while (got < frames)
        {
            if (_readFrame < _decoderFrame)
            {
                int count = (int)Math.Min(_decoderFrame - _readFrame, frames - got);
                int ring = (int)(_readFrame % HistoryFrames);
                count = Math.Min(count, HistoryFrames - ring);
                _history.AsSpan(ring * channels, count * channels).CopyTo(_decoded.AsSpan(got * channels));
                got += count;
                _readFrame += count;
                continue;
            }

            int want = frames - got;
            Span<float> destination = _decoded.AsSpan(got * channels, want * channels);
            int read = decoder.Read(destination);
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

    private void StoreHistory(ReadOnlySpan<float> samples, int channels)
    {
        int frames = samples.Length / channels;
        int done = 0;
        while (done < frames)
        {
            int ring = (int)((_decoderFrame + done) % HistoryFrames);
            int count = Math.Min(frames - done, HistoryFrames - ring);
            samples.Slice(done * channels, count * channels).CopyTo(_history.AsSpan(ring * channels));
            done += count;
        }
        _decoderFrame += frames;
        _historyCount = Math.Min(HistoryFrames, _historyCount + frames);
    }

    // The same steps as AudioSourceComponent.StopAndRewindAllSources, on the cached list, so no enumeration allocates.
    private void StopSources()
    {
        for (int i = 0; i < _sourceCount; i++)
        {
            AudioSource source = _sources[i];
            source.Stop();
            source.UnqueueConsumedBuffers();
            source.Rewind();
        }
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
    private static short ToShort(float sample)
        => (short)Math.Clamp(MathF.Round(sample * 32768.0f), short.MinValue, short.MaxValue);
}
