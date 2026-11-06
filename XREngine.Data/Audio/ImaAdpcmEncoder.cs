namespace XREngine.Data;

/// <summary>
/// Encodes mono float samples to standard IMA ADPCM with 4 bits for each sample (see <see cref="ImaAdpcmDecoder"/> for
/// the block layout). Each block header stores the first sample and the step index, so a seek decodes one block only.
/// </summary>
/// <remarks>
/// The encoder quantizes each sample to 16 bits with rounding to the nearest value, and it tracks the decoder state, so
/// the error does not grow along a block. The step index continues from the end of the block before. The first block
/// starts with the step index whose step is nearest to the first difference, so the start of the signal does not wait
/// for the step to grow. The output is deterministic.
/// </remarks>
public static class ImaAdpcmEncoder
{
    /// <summary>Encodes mono samples.</summary>
    /// <param name="mono">The samples, in the range -1 to 1. A value outside the range is clipped, and NaN is zero.</param>
    /// <param name="blockSamples">The number of samples in each full block. It must be odd. The last block can be shorter.</param>
    /// <returns>The encoded blocks. The length is <see cref="GetEncodedBytes"/> of the sample count.</returns>
    public static byte[] Encode(ReadOnlySpan<float> mono, int blockSamples)
    {
        ImaAdpcm.ValidateBlockSamples(blockSamples, nameof(blockSamples));

        byte[] data = new byte[ImaAdpcm.GetEncodedBytes(mono.Length, blockSamples)];
        int stepIndex = mono.Length > 1 ? InitialStepIndex(ToPcm16(mono[1]) - ToPcm16(mono[0])) : 0;
        int offset = 0;
        for (int start = 0; start < mono.Length; start += blockSamples)
        {
            int count = Math.Min(blockSamples, mono.Length - start);
            EncodeBlock(mono.Slice(start, count), data.AsSpan(offset, ImaAdpcm.GetBlockBytes(count)), ref stepIndex);
            offset += ImaAdpcm.GetBlockBytes(count);
        }
        return data;
    }

    /// <summary>
    /// Gets the length of the encoded data of a number of mono frames: <c>4 + (n - 1) / 2</c> bytes, rounded up, for each
    /// block of <c>n</c> samples.
    /// </summary>
    /// <param name="frames">The number of mono frames.</param>
    /// <param name="blockSamples">The number of samples in each full block. It must be odd.</param>
    public static long GetEncodedBytes(long frames, int blockSamples)
    {
        ImaAdpcm.ValidateBlockSamples(blockSamples, nameof(blockSamples));
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        return ImaAdpcm.GetEncodedBytes(frames, blockSamples);
    }

    /// <summary>Converts a float sample to a 16 bit sample, with rounding to the nearest value and clipping.</summary>
    /// <param name="sample">The sample, in the range -1 to 1.</param>
    public static int ToPcm16(float sample)
    {
        if (float.IsNaN(sample))
            return 0;
        return (int)Math.Clamp(MathF.Round(sample * 32768.0f, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
    }

    private static void EncodeBlock(ReadOnlySpan<float> samples, Span<byte> block, ref int stepIndex)
    {
        int predictor = ToPcm16(samples[0]);
        block[0] = (byte)predictor;
        block[1] = (byte)(predictor >> 8);
        block[2] = (byte)stepIndex;
        block[3] = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            int code = Quantize(ToPcm16(samples[i]) - predictor, ImaAdpcm.StepTable[stepIndex]);
            ImaAdpcm.Step(code, ref predictor, ref stepIndex);

            int byteIndex = ImaAdpcm.HeaderBytes + ((i - 1) >> 1);
            if (((i - 1) & 1) == 0)
                block[byteIndex] = (byte)code;
            else
                block[byteIndex] |= (byte)(code << 4);
        }
    }

    // The standard IMA ADPCM quantizer: the sign bit, then one bit each for the step, half the step, and a quarter step.
    private static int Quantize(int difference, int step)
    {
        int code = 0;
        if (difference < 0)
        {
            code = 8;
            difference = -difference;
        }
        if (difference >= step)
        {
            code |= 4;
            difference -= step;
        }
        step >>= 1;
        if (difference >= step)
        {
            code |= 2;
            difference -= step;
        }
        step >>= 1;
        if (difference >= step)
            code |= 1;
        return code;
    }

    private static int InitialStepIndex(int difference)
    {
        int magnitude = Math.Abs(difference);
        short[] steps = ImaAdpcm.StepTable;
        int best = 0;
        for (int i = 1; i <= ImaAdpcm.MaxStepIndex; i++)
        {
            if (Math.Abs(steps[i] - magnitude) < Math.Abs(steps[best] - magnitude))
                best = i;
        }
        return best;
    }
}
