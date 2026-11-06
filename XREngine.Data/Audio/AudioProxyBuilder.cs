namespace XREngine.Data;

/// <summary>
/// Makes an <see cref="AudioProxyAsset"/> from any <see cref="IStreamingAudioDecoder"/>. The source can have any number
/// of channels and any sample rate. The builder mixes the channels to mono and resamples the result to
/// <see cref="ImaAdpcmDecoder.ProxySampleRate"/>, and then encodes it with <see cref="ImaAdpcmEncoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mix is the mean of the channels of each frame. The resampler is linear: output frame <c>i</c> is at source
/// position <c>i * sourceRate / targetRate</c>, which the builder computes with integers, so the result is deterministic
/// and does not drift on long files. Between two source frames it interpolates on a straight line, and after the last
/// source frame it holds that frame. A linear resampler has no low-pass filter, so a source above 44.1 kHz can give some
/// alias noise above 11 kHz. That is acceptable for a short preview of the sound during a seek. The output has
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

        long expected = source.TotalFrames > 0 ? CeilDiv(source.TotalFrames * targetSampleRate, sourceRate) : 0;
        List<float> output = new((int)Math.Min(expected + 1, int.MaxValue / 2));
        float[] interleaved = new float[ReadFrames * channels];
        float[] mono = new float[ReadFrames];

        source.SeekToFrame(0);
        long baseFrame = 0;
        float previous = 0.0f;
        long next = 0;
        while (true)
        {
            int read = source.Read(interleaved);
            if (read <= 0)
                break;

            Downmix(interleaved, channels, mono, read);
            long end = baseFrame + read;

            // Output frame i needs source frames idx and idx + 1. Write each frame whose two source frames are known.
            while (true)
            {
                long position = next * sourceRate;
                long index = position / targetSampleRate;
                if (index + 1 >= end)
                    break;

                float first = index < baseFrame ? previous : mono[index - baseFrame];
                float second = mono[index + 1 - baseFrame];
                float fraction = (float)(position - index * targetSampleRate) / targetSampleRate;
                output.Add(first + (second - first) * fraction);
                next++;
            }

            previous = mono[read - 1];
            baseFrame = end;
        }

        // The last frames sit at or after the last source frame, so they hold it.
        long total = CeilDiv(baseFrame * targetSampleRate, sourceRate);
        for (; next < total; next++)
            output.Add(previous);
        return [.. output];
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

    private static long CeilDiv(long value, long divisor)
        => (value + divisor - 1) / divisor;
}
