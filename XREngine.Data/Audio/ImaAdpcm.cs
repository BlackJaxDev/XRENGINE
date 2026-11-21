namespace XREngine.Data;

/// <summary>
/// The shared data of the IMA ADPCM format: the step table, the index table, the block layout, and the nibble step.
/// The encoder and the decoder use the same integer steps, so the decoder output is the same on every machine.
/// </summary>
/// <remarks>
/// A mono block starts with a 4 byte header: the first sample as a little-endian <see cref="short"/>, the step index
/// (0 to 88), and one reserved zero byte. Then each byte holds two 4 bit codes, the earlier sample in the low nibble.
/// A block of <c>n</c> samples therefore has <c>4 + (n - 1) / 2</c> bytes, and <c>n</c> must be odd. Because each header
/// stores the full decoder state, a block decodes with no data from the blocks before it.
/// </remarks>
internal static class ImaAdpcm
{
    /// <summary>The number of bytes in a block header.</summary>
    public const int HeaderBytes = 4;

    /// <summary>The largest step index.</summary>
    public const int MaxStepIndex = 88;

    /// <summary>The 89 quantizer step sizes of the standard IMA ADPCM format. Do not change the items.</summary>
    public static readonly short[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    /// <summary>The 16 step index changes of the standard IMA ADPCM format, one for each 4 bit code. Do not change the items.</summary>
    public static readonly sbyte[] IndexTable =
    [
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    ];

    /// <summary>Checks a block size. A block must hold an odd number of samples, because the header holds one sample.</summary>
    /// <param name="blockSamples">The number of samples in each full block.</param>
    /// <param name="paramName">The parameter name for the exception.</param>
    public static void ValidateBlockSamples(int blockSamples, string paramName)
    {
        if (blockSamples < 1 || (blockSamples & 1) == 0)
            throw new ArgumentOutOfRangeException(paramName, blockSamples, "An IMA ADPCM block must hold an odd number of samples, one or more.");
    }

    /// <summary>Gets the number of bytes of a block that holds a number of samples.</summary>
    /// <param name="samples">The number of samples in the block, one or more.</param>
    public static int GetBlockBytes(int samples)
        => HeaderBytes + samples / 2;

    /// <summary>Gets the number of bytes of the encoded data of a number of frames.</summary>
    /// <param name="frames">The number of mono frames.</param>
    /// <param name="blockSamples">The number of samples in each full block.</param>
    public static long GetEncodedBytes(long frames, int blockSamples)
    {
        long fullBlocks = frames / blockSamples;
        int tail = (int)(frames - fullBlocks * blockSamples);
        return fullBlocks * GetBlockBytes(blockSamples) + (tail > 0 ? GetBlockBytes(tail) : 0);
    }

    /// <summary>
    /// Gets the largest number of frames that encoded data can hold. Each 4 bit code counts as one frame, so a partial
    /// last block with an odd number of codes gives one frame more than the encoder wrote.
    /// </summary>
    /// <param name="dataBytes">The length of the encoded data.</param>
    /// <param name="blockSamples">The number of samples in each full block.</param>
    public static long GetMaxFrames(long dataBytes, int blockSamples)
    {
        int blockBytes = GetBlockBytes(blockSamples);
        long fullBlocks = dataBytes / blockBytes;
        long tailBytes = dataBytes - fullBlocks * blockBytes;
        long frames = fullBlocks * blockSamples;
        if (tailBytes >= HeaderBytes)
            frames += 1 + (tailBytes - HeaderBytes) * 2;
        return frames;
    }

    /// <summary>Applies one 4 bit code to the decoder state. The encoder calls this too, so both keep the same state.</summary>
    /// <param name="code">The 4 bit code.</param>
    /// <param name="predictor">The last sample. It receives the new sample.</param>
    /// <param name="stepIndex">The step index. It receives the new step index.</param>
    public static void Step(int code, ref int predictor, ref int stepIndex)
    {
        int step = StepTable[stepIndex];
        int delta = step >> 3;
        if ((code & 4) != 0)
            delta += step;
        if ((code & 2) != 0)
            delta += step >> 1;
        if ((code & 1) != 0)
            delta += step >> 2;

        predictor = Math.Clamp((code & 8) != 0 ? predictor - delta : predictor + delta, short.MinValue, short.MaxValue);
        stepIndex = Math.Clamp(stepIndex + IndexTable[code], 0, MaxStepIndex);
    }
}
