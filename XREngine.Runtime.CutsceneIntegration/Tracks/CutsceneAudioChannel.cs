using XREngine.Audio;
using XREngine.Components;
using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>
/// The streaming state of one audio track. The channel owns the decoder of the active clip and feeds the
/// sources of the bound <see cref="AudioSourceComponent"/> through <see cref="AudioSourceComponent.EnqueueStreamingBuffers(int, bool, short[][])"/>.
/// Each buffer holds <see cref="AudioTrackEvaluator.ChunkFrames"/> frames. The last buffer of a clip is padded with silence,
/// so every queued buffer has the same length and the play position follows from the number of queued buffers.
/// </summary>
internal sealed class CutsceneAudioChannel
{
    private const int MaxChannels = 2;
    private const int QueueTarget = 4;
    private const int ChunkFrames = AudioTrackEvaluator.ChunkFrames;

    private readonly float[] _decoded = new float[ChunkFrames * MaxChannels];
    private readonly short[] _mono = new short[ChunkFrames];
    private readonly short[] _stereo = new short[ChunkFrames * MaxChannels];
    private readonly short[][] _monoBatch;
    private readonly short[][] _stereoBatch;

    private AudioSourceComponent? _component;
    private bool _savedExternalBufferManagement;
    private AudioSource[] _sources = [];
    private int _sourceCount;

    private IStreamingAudioDecoder? _decoder;
    private long _endFrame;
    private long _nextFrame;
    private bool _endOfData;
    private bool _streaming;
    private float _appliedGain = float.NaN;

    public CutsceneAudioChannel(int trackIndex, AudioTrack track)
    {
        TrackIndex = trackIndex;
        Track = track;
        _monoBatch = [_mono];
        _stereoBatch = [_stereo];
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

    /// <summary>True if the last service restarted the stream, so the queue is new and the clock has no stable value yet.</summary>
    public bool RestartedThisFrame { get; private set; }

    /// <summary>The number of sources that the channel feeds.</summary>
    public int SourceCount => _sourceCount;

    /// <summary>
    /// Takes control of the buffers of a component. A different component releases the earlier one first.
    /// While the channel holds a component, the component does not unqueue buffers itself and does not play on a queue.
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
    }

    /// <summary>
    /// Keeps the sources at the clip frame. A restart stops the sources, seeks the decoder, fills the queue, and plays again.
    /// Without a restart, the channel unqueues played buffers and adds new ones.
    /// </summary>
    /// <param name="targetFrame">The frame in the file at the cutscene time.</param>
    /// <param name="restart">True to seek even if the stream plays, for example after a jump.</param>
    /// <param name="gain">The gain to set on each source.</param>
    public void Service(long targetFrame, bool restart, float gain)
    {
        RestartedThisFrame = false;
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

        if (!_streaming || restart)
            Restart(targetFrame);
        else
            Pump();
        ApplyGain(gain);
    }

    /// <summary>
    /// Reads the frame of the clip file that the listener hears now, from the clock of the first source.
    /// The value is the number of frames before the queue, plus the source position, minus the output latency.
    /// </summary>
    /// <param name="frame">The heard frame in the file.</param>
    /// <returns>False if the transport has no clock for the source.</returns>
    public bool TryReadHeardFrame(out long frame)
    {
        frame = 0;
        if (_sourceCount <= 0 || _decoder is null)
            return false;

        AudioSource primary = _sources[0];
        if (!primary.TryGetPlaybackClock(out long sampleFrame, out long latencyNanoseconds))
            return false;

        long queueStart = _nextFrame - (long)primary.BuffersQueued * ChunkFrames;
        long latencyFrames = (long)((Int128)latencyNanoseconds * _decoder.SampleRate / 1_000_000_000L);
        frame = Math.Max(0L, queueStart + sampleFrame - latencyFrames);
        return true;
    }

    /// <summary>Stops the sources and drops the queue, but keeps the decoder. The next service restarts the stream.</summary>
    public void Silence()
    {
        if (_component is null)
            return;

        if (_streaming)
            _component.StopAndRewindAllSources();
        _streaming = false;
        ApplyGain(0.0f);
    }

    /// <summary>Stops the sources, closes the decoder, and gives the component back with its earlier settings.</summary>
    public void Release()
    {
        bool streaming = _streaming;
        CloseDecoder();
        AudioSourceComponent? component = _component;
        if (component is null)
            return;

        if (streaming)
            component.StopAndRewindAllSources();
        for (int i = 0; i < _sourceCount; i++)
        {
            _sources[i].AutoPlayOnQueue = true;
            _sources[i].Gain = component.Gain;
        }
        component.ExternalBufferManagement = _savedExternalBufferManagement;
        _component = null;
        _sourceCount = 0;
        Array.Clear(_sources);
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
    }

    // Rebuilds the source list when the number of listeners changes. The enumeration of the listener table allocates,
    // so it runs only on a change and not in each frame.
    private bool RefreshSources()
    {
        AudioSourceComponent component = _component!;
        int count = component.ActiveListenerCount;
        if (count == _sourceCount)
            return false;

        if (_sources.Length < count)
            _sources = new AudioSource[count];
        Array.Clear(_sources);
        int index = 0;
        foreach (KeyValuePair<ListenerContext, AudioSource> pair in component.ActiveListeners)
        {
            if (index >= _sources.Length)
                break;
            AudioSource source = pair.Value;
            source.AutoPlayOnQueue = false;
            _sources[index++] = source;
        }
        _sourceCount = index;
        _appliedGain = float.NaN;
        return true;
    }

    private void Restart(long targetFrame)
    {
        AudioSourceComponent component = _component!;
        IStreamingAudioDecoder decoder = _decoder!;
        component.StopAndRewindAllSources();

        long frame = Math.Clamp(targetFrame, 0L, _endFrame);
        decoder.SeekToFrame(frame);
        _nextFrame = frame;
        _endOfData = frame >= _endFrame;
        Fill();

        _streaming = true;
        RestartedThisFrame = true;
        if (_sources[0].BuffersQueued > 0)
            component.PlayStoppedSources();
    }

    private void Pump()
    {
        for (int i = 0; i < _sourceCount; i++)
        {
            AudioSource source = _sources[i];
            if (source.BuffersProcessed > 0)
                source.UnqueueConsumedBuffers();
        }

        Fill();

        // A source that ran out of buffers stops. Start it again when new buffers are in its queue.
        AudioSource primary = _sources[0];
        if (!primary.IsPlaying && primary.BuffersQueued > 0)
            _component!.PlayStoppedSources();
    }

    private void Fill()
    {
        IStreamingAudioDecoder decoder = _decoder!;
        AudioSourceComponent component = _component!;
        int channels = decoder.Channels;
        int target = Math.Min(QueueTarget, Math.Max(1, component.MaxStreamingBuffers));
        bool stereo = channels == 2;
        short[] pcm = stereo ? _stereo : _mono;
        short[][] batch = stereo ? _stereoBatch : _monoBatch;

        while (!_endOfData && _sources[0].BuffersQueued < target)
        {
            int wanted = (int)Math.Min(ChunkFrames, _endFrame - _nextFrame);
            int frames = wanted > 0 ? decoder.Read(_decoded.AsSpan(0, wanted * channels)) : 0;
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

            if (!component.EnqueueStreamingBuffers(decoder.SampleRate, stereo, batch))
                break;
            _nextFrame += ChunkFrames;

            // A short read means the end of the clip or of the file, so the decoder position and the frame count stay equal.
            if (frames < ChunkFrames || _nextFrame >= _endFrame)
                _endOfData = true;
        }
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
