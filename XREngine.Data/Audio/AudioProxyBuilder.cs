namespace XREngine.Data;

/// <summary>
/// Makes an <see cref="AudioProxyAsset"/> from any <see cref="IStreamingAudioDecoder"/>. The source can have any number
/// of channels and any sample rate. The builder mixes the channels to mono and resamples the result to
/// <see cref="ImaAdpcmDecoder.ProxySampleRate"/>, and then encodes it with <see cref="ImaAdpcmEncoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mix is the mean of the channels of each frame. When the source rate is above the target rate, a low-pass FIR
/// filter (<see cref="AudioProxyLowPassFilter"/>, cutoff at 0.9 times the target Nyquist frequency) runs first. Without
/// it, a 44.1 kHz source would be a plain 2:1 decimation, and all content from 11 to 22 kHz (for example sibilance and
/// cymbals) would fold back below 11 kHz, into the band that the listener hears. Then a linear resampler
/// (<see cref="AudioProxyLinearResampler"/>) computes output frame <c>i</c> at source position
/// <c>i * sourceRate / targetRate</c> with integers, so the result is deterministic and does not drift. The output has
/// <c>ceil(n * targetRate / sourceRate)</c> frames for <c>n</c> source frames.
/// </para>
/// <para>
/// The builder reads the source in parts, but it keeps the mono result in memory until the encode: about 88 KB for each
/// second of audio. Run it at a bake and not in a frame.
/// </para>
/// </remarks>
public static class AudioProxyBuilder
{
    private const int ReadFrames = 4096;

    /// <summary>Makes a proxy from a decoder. The builder seeks the decoder to frame zero first.</summary>
    /// <param name="source">The decoder of the source file.</param>
    /// <param name="sourceReference">The stable reference of the source file (see <see cref="AudioProxyAsset.SourceReference"/>).</param>
    /// <param name="sourceContentHash">The hash of the source file, or zero.</param>
    /// <param name="blockSamples">The number of samples in each block. It must be odd.</param>
    public static AudioProxyAsset Build(
        IStreamingAudioDecoder source,
        string sourceReference,
        ulong sourceContentHash,
        int blockSamples = AudioProxyAsset.DefaultBlockSamples)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceReference);
        ImaAdpcm.ValidateBlockSamples(blockSamples, nameof(blockSamples));

        float[] mono = ToMono(source, ImaAdpcmDecoder.ProxySampleRate);
        return new AudioProxyAsset
        {
            Name = Path.GetFileNameWithoutExtension(sourceReference),
            EncodedData = ImaAdpcmEncoder.Encode(mono, blockSamples),
            SampleRate = ImaAdpcmDecoder.ProxySampleRate,
            BlockSamples = blockSamples,
            FrameCount = mono.Length,
            SourceReference = sourceReference,
            SourceContentHash = sourceContentHash,
        };
    }

    /// <summary>Decodes a source, mixes it to mono, and resamples it linearly to a sample rate.</summary>
    /// <param name="source">The decoder. The method seeks it to frame zero first.</param>
    /// <param name="targetSampleRate">The output sample rate, in frames per second.</param>
    /// <returns>The mono samples at the output rate.</returns>
    public static float[] ToMono(IStreamingAudioDecoder source, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);
        int channels = source.Channels;
        int sourceRate = source.SampleRate;
        if (channels <= 0 || sourceRate <= 0)
            throw new InvalidDataException($"The audio source has {channels} channels at {sourceRate} Hz. A proxy needs one or more channels and a positive sample rate.");

        bool filter = sourceRate > targetSampleRate;
        AudioProxyLowPassFilter? lowPass = filter ? new AudioProxyLowPassFilter(sourceRate, targetSampleRate, ReadFrames) : null;
        AudioProxyLinearResampler resampler = new(sourceRate, targetSampleRate, source.TotalFrames);
        float[] interleaved = new float[ReadFrames * channels];
        float[] mono = new float[ReadFrames];
        float[] filtered = new float[Math.Max(ReadFrames, lowPass?.HalfLength ?? 0)];

        source.SeekToFrame(0);
        while (true)
        {
            int read = source.Read(interleaved);
            if (read <= 0)
                break;

            Downmix(interleaved, channels, mono, read);
            if (lowPass is null)
            {
                resampler.Push(mono.AsSpan(0, read));
                continue;
            }

            int count = lowPass.Process(mono.AsSpan(0, read), filtered);
            resampler.Push(filtered.AsSpan(0, count));
        }

        if (lowPass is not null)
            resampler.Push(filtered.AsSpan(0, lowPass.Flush(filtered)));
        return resampler.Finish();
    }

    private static void Downmix(float[] interleaved, int channels, float[] mono, int frames)
    {
        if (channels == 1)
        {
            interleaved.AsSpan(0, frames).CopyTo(mono);
            return;
        }

        float scale = 1.0f / channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0.0f;
            int first = f * channels;
            for (int c = 0; c < channels; c++)
                sum += interleaved[first + c];
            mono[f] = sum * scale;
        }
    }
}
