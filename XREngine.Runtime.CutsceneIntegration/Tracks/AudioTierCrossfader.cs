using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>
/// Makes the audio of a track at the rate and the channel count of the full decoder from the audio proxy, and crossfades
/// from the proxy to the full decoder. The proxy is mono IMA ADPCM at <see cref="ImaAdpcmDecoder.ProxySampleRate"/>.
/// The crossfader resamples it on a straight line and copies the value to each output channel, so the queue of the
/// sources keeps one format and one frame rate across the tier change, and the frame count of the clock does not change.
/// </summary>
/// <remarks>
/// <para>
/// Full frame <c>f</c> is at proxy position <c>f * proxyRate / fullRate</c>, which the crossfader computes with integers.
/// The proxy bake puts proxy frame <c>i</c> at source position <c>i * sourceRate / proxyRate</c> with no filter delay,
/// so the two signals are sample-aligned.
/// </para>
/// <para>
/// The crossfade lasts <see cref="CrossfadeSeconds"/> and uses equal-gain (linear) gains that sum to one at each frame.
/// The proxy and the full decoder are coherent, sample-aligned signals, so equal gain keeps the level flat. Equal power
/// would make the middle of the fade up to 3 dB louder and could clip. The caller starts it at a
/// buffer boundary. All buffers are allocated when a clip opens, so a read does not allocate.
/// </para>
/// </remarks>
internal sealed class AudioTierCrossfader
{
    /// <summary>The length of the crossfade from the proxy to the full decoder, in seconds.</summary>
    public const double CrossfadeSeconds = 0.020;

    private ImaAdpcmDecoder? _proxy;
    private int _proxyRate;
    private int _fullRate = 1;
    private int _channels = 1;
    private int _maxFrames;
    private float[] _proxyFrames = [];
    private float[] _mix = [];

    /// <summary>True if a proxy decoder is open.</summary>
    public bool HasProxy => _proxy is not null;

    /// <summary>The length of the crossfade in frames of the full rate.</summary>
    public int CrossfadeFrames { get; private set; } = 1;

    /// <summary>Opens the crossfader for one clip. It owns the proxy decoder until <see cref="Close"/>.</summary>
    /// <param name="proxy">The decoder of the proxy, or null to give silence in place of the proxy.</param>
    /// <param name="fullRate">The sample rate of the full decoder.</param>
    /// <param name="channels">The channel count of the full decoder.</param>
    /// <param name="maxFrames">The largest number of frames of one read.</param>
    public void Open(ImaAdpcmDecoder? proxy, int fullRate, int channels, int maxFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fullRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);

        Close();
        _proxy = proxy;
        _fullRate = fullRate;
        _channels = channels;
        _maxFrames = maxFrames;
        CrossfadeFrames = Math.Max(1, (int)Math.Round(CrossfadeSeconds * fullRate));

        int mixFrames = Math.Min(maxFrames, CrossfadeFrames);
        if (_mix.Length < mixFrames * channels)
            _mix = new float[mixFrames * channels];

        if (proxy is null)
            return;

        // One read needs the proxy frames from the first position to one frame after the last position.
        _proxyRate = proxy.SampleRate;
        int needed = (int)((long)maxFrames * _proxyRate / fullRate) + 3;
        if (_proxyFrames.Length < needed)
            _proxyFrames = new float[needed];
    }

    /// <summary>Closes the proxy decoder. The buffers stay for the next clip.</summary>
    public void Close()
    {
        _proxy?.Dispose();
        _proxy = null;
    }

    /// <summary>
    /// Writes the proxy at the full rate and the full channel count, starting at a frame of the full rate.
    /// With no proxy, the frames are silent. A position after the end of the proxy holds its last frame.
    /// </summary>
    /// <param name="fullFrame">The first frame, in frames of the full rate.</param>
    /// <param name="destination">Receives the interleaved samples. It must hold <paramref name="frames"/> frames.</param>
    /// <param name="frames">The number of frames to write.</param>
    public void ReadProxy(long fullFrame, Span<float> destination, int frames)
    {
        if (frames <= 0)
            return;

        if (_proxy is null)
        {
            destination[..(frames * _channels)].Clear();
            return;
        }

        int done = 0;
        while (done < frames)
        {
            int count = Math.Min(frames - done, _maxFrames);
            ReadProxyPart(fullFrame + done, destination.Slice(done * _channels, count * _channels), count);
            done += count;
        }
    }

    /// <summary>
    /// Mixes the start of the full decoder output with the proxy. Frame <c>k</c> of the crossfade gets the full gain
    /// <c>t = (k + 0.5) / CrossfadeFrames</c> and the proxy gain <c>1 - t</c>.
    /// </summary>
    /// <param name="full">The interleaved output of the full decoder. The method changes it in place.</param>
    /// <param name="fullFrame">The frame of the first sample of <paramref name="full"/>.</param>
    /// <param name="frames">The number of frames in <paramref name="full"/>.</param>
    /// <param name="done">The number of crossfade frames done before. The method adds the frames that it mixes.</param>
    /// <param name="fromSilence">True to fade in from silence and not from the proxy.</param>
    public void Crossfade(Span<float> full, long fullFrame, int frames, ref int done, bool fromSilence)
    {
        int channels = _channels;
        while (frames > 0 && done < CrossfadeFrames)
        {
            int count = Math.Min(Math.Min(frames, CrossfadeFrames - done), _mix.Length / channels);
            Span<float> mix = _mix.AsSpan(0, count * channels);
            if (fromSilence)
                mix.Clear();
            else
                ReadProxy(fullFrame, mix, count);

            for (int i = 0; i < count; i++)
            {
                GetGains(done + i, CrossfadeFrames, out float fadeOut, out float fadeIn);
                int offset = i * channels;
                for (int c = 0; c < channels; c++)
                    full[offset + c] = full[offset + c] * fadeIn + mix[offset + c] * fadeOut;
            }

            done += count;
            frames -= count;
            fullFrame += count;
            full = full[(count * channels)..];
        }
    }

    /// <summary>
    /// Gets the equal-gain (linear) gains of one crossfade frame. The sum of the gains is one.
    /// </summary>
    /// <param name="index">The frame index in the crossfade, from zero.</param>
    /// <param name="length">The length of the crossfade in frames.</param>
    /// <param name="fadeOut">The gain of the signal that fades out.</param>
    /// <param name="fadeIn">The gain of the signal that fades in.</param>
    public static void GetGains(int index, int length, out float fadeOut, out float fadeIn)
    {
        fadeIn = (float)((index + 0.5) / length);
        fadeOut = 1.0f - fadeIn;
    }

    private void ReadProxyPart(long fullFrame, Span<float> destination, int frames)
    {
        ImaAdpcmDecoder proxy = _proxy!;
        long proxyRate = _proxyRate;
        long fullRate = _fullRate;
        long first = fullFrame * proxyRate / fullRate;
        long last = (fullFrame + frames - 1) * proxyRate / fullRate + 1;
        int needed = (int)(last - first + 1);

        proxy.SeekToFrame(first);
        int got = proxy.Read(_proxyFrames.AsSpan(0, needed));
        float hold = got > 0 ? _proxyFrames[got - 1] : 0.0f;
        for (int i = got; i < needed; i++)
            _proxyFrames[i] = hold;

        int channels = _channels;
        for (int i = 0; i < frames; i++)
        {
            long position = (fullFrame + i) * proxyRate;
            long index = position / fullRate;
            float fraction = (float)(position - index * fullRate) / fullRate;
            int k = (int)(index - first);
            float a = _proxyFrames[k];
            float value = a + (_proxyFrames[k + 1] - a) * fraction;
            int offset = i * channels;
            for (int c = 0; c < channels; c++)
                destination[offset + c] = value;
        }
    }
}
