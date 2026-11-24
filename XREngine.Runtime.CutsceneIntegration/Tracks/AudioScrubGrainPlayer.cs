namespace XREngine.Cutscenes;

/// <summary>
/// Makes the short grains that a track plays from the audio proxy while the playhead moves in a scrub.
/// A grain is <see cref="GrainSeconds"/> long and starts at the playhead. It fades in and out over
/// <see cref="FadeSeconds"/> at each end, so its first and its last frame have zero amplitude and it does not click.
/// </summary>
/// <remarks>
/// The grain has the rate and the channel count of the full decoder, from <see cref="AudioTierCrossfader.ReadProxy"/>.
/// <see cref="Configure"/> allocates the buffers when a clip opens. <see cref="Build"/> writes into them, so a grain does not allocate.
/// </remarks>
internal sealed class AudioScrubGrainPlayer
{
    /// <summary>The length of one grain, in seconds.</summary>
    public const double GrainSeconds = 0.060;

    /// <summary>The length of the fade at each end of a grain, in seconds.</summary>
    public const double FadeSeconds = 0.010;

    private float[] _samples = [];
    private short[] _pcm = [];
    private int _channels = 1;

    /// <summary>The number of frames in one grain.</summary>
    public int GrainFrames { get; private set; }

    /// <summary>The number of frames of the fade at each end.</summary>
    public int FadeFrames { get; private set; }

    /// <summary>Sets the format of the grains. A new format allocates new buffers. The same format keeps them.</summary>
    /// <param name="sampleRate">The sample rate of the full decoder.</param>
    /// <param name="channels">The channel count of the full decoder.</param>
    public void Configure(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        GrainFrames = Math.Max(2, (int)Math.Round(GrainSeconds * sampleRate));
        FadeFrames = Math.Clamp((int)Math.Round(FadeSeconds * sampleRate), 1, GrainFrames / 2);
        _channels = channels;

        // The transport reads the whole array as the buffer, so the array has the exact length of one grain.
        int samples = GrainFrames * channels;
        if (_pcm.Length != samples)
        {
            _pcm = new short[samples];
            _samples = new float[samples];
        }
    }

    /// <summary>Writes the grain at a frame into the buffer of the player and returns that buffer.</summary>
    /// <param name="source">The crossfader that reads the proxy.</param>
    /// <param name="frame">The first frame of the grain, in frames of the full rate.</param>
    /// <returns>The 16-bit interleaved samples of the grain. The next call writes the same array.</returns>
    public short[] Build(AudioTierCrossfader source, long frame)
    {
        source.ReadProxy(frame, _samples, GrainFrames);
        int channels = _channels;
        for (int i = 0; i < GrainFrames; i++)
        {
            float gain = GetFadeGain(i, GrainFrames, FadeFrames);
            int offset = i * channels;
            for (int c = 0; c < channels; c++)
                _pcm[offset + c] = CutsceneAudioChannel.ToShort(_samples[offset + c] * gain);
        }
        return _pcm;
    }

    /// <summary>
    /// Gets the gain of one frame of a grain: a squared sine from zero to one over the fade at the start, one in the middle,
    /// and the mirror of the start fade at the end. The first and the last frame have the gain zero.
    /// </summary>
    /// <param name="index">The frame index in the grain.</param>
    /// <param name="frames">The number of frames in the grain.</param>
    /// <param name="fadeFrames">The number of frames of each fade.</param>
    public static float GetFadeGain(int index, int frames, int fadeFrames)
    {
        int fromEnd = frames - 1 - index;
        int edge = Math.Min(index, fromEnd);
        if (edge >= fadeFrames)
            return 1.0f;

        double s = Math.Sin(edge / (double)fadeFrames * (Math.PI / 2.0));
        return (float)(s * s);
    }
}
