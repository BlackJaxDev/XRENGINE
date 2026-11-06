namespace XREngine.Data;

/// <summary>
/// Decodes mono IMA ADPCM data from memory (see <see cref="ImaAdpcmEncoder"/>). A seek decodes only the block of the new
/// frame, because each block header stores the full decoder state. A random-access read therefore gives exactly the
/// same samples as a sequential read.
/// </summary>
/// <remarks>
/// A mono block starts with a 4 byte header: the first sample as a little-endian 16 bit value, the step index (0 to 88),
/// and one reserved byte. Then each byte holds two 4 bit codes, the earlier sample in the low nibble. The decoder keeps
/// one decoded block. It allocates the block buffer in the constructor, so <see cref="Read"/> does not allocate.
/// </remarks>
public sealed class ImaAdpcmDecoder : IStreamingAudioDecoder
{
    /// <summary>The sample rate of an audio proxy, in frames per second.</summary>
    public const int ProxySampleRate = 22050;

    private readonly byte[] _data;
    private readonly int _blockSamples;
    private readonly int _blockBytes;
    private readonly float[] _block;
    private long _decodedBlock = -1;
    private long _frame;
    private bool _disposed;

    /// <summary>
    /// Reads encoded data at <see cref="ProxySampleRate"/>. The frame count comes from the data length, so a partial last
    /// block with an odd number of codes gives one more frame than the encoder wrote. Use the other constructor to give
    /// the exact count.
    /// </summary>
    /// <param name="data">The encoded blocks. The decoder does not copy them, so do not change them while it reads.</param>
    /// <param name="blockSamples">The number of samples in each full block. It must be odd.</param>
    public ImaAdpcmDecoder(byte[] data, int blockSamples)
        : this(data, blockSamples, MaxFrames(data, blockSamples), ProxySampleRate)
    {
    }

    /// <summary>Reads encoded data with a known frame count and sample rate.</summary>
    /// <param name="data">The encoded blocks. The decoder does not copy them, so do not change them while it reads.</param>
    /// <param name="blockSamples">The number of samples in each full block. It must be odd.</param>
    /// <param name="frameCount">The number of frames. It must not be more than the data holds.</param>
    /// <param name="sampleRate">The sample rate, in frames per second.</param>
    /// <exception cref="InvalidDataException">A block header has a step index above 88.</exception>
    public ImaAdpcmDecoder(byte[] data, int blockSamples, long frameCount, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(data);
        ImaAdpcm.ValidateBlockSamples(blockSamples, nameof(blockSamples));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        if (frameCount > ImaAdpcm.GetMaxFrames(data.Length, blockSamples))
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "The frame count is more than the IMA ADPCM data holds.");

        _data = data;
        _blockSamples = blockSamples;
        _blockBytes = ImaAdpcm.GetBlockBytes(blockSamples);
        _block = new float[blockSamples];
        SampleRate = sampleRate;
        TotalFrames = frameCount;

        // A bad step index would read outside the step table, so check each header one time here.
        for (long offset = 0; offset + ImaAdpcm.HeaderBytes <= data.Length; offset += _blockBytes)
            if (data[offset + 2] > ImaAdpcm.MaxStepIndex)
                throw new InvalidDataException($"The IMA ADPCM block at byte {offset} has the step index {data[offset + 2]}. The largest valid index is {ImaAdpcm.MaxStepIndex}.");
    }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public int Channels => 1;

    /// <inheritdoc/>
    public long TotalFrames { get; }

    /// <summary>The number of samples in each full block.</summary>
    public int BlockSamples => _blockSamples;

    /// <inheritdoc/>
    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frame = Math.Clamp(frame, 0L, TotalFrames);
    }

    /// <inheritdoc/>
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int frames = (int)Math.Min(destination.Length, TotalFrames - _frame);
        int written = 0;
        while (written < frames)
        {
            long block = _frame / _blockSamples;
            if (block != _decodedBlock)
                DecodeBlock(block);

            int first = (int)(_frame - block * _blockSamples);
            int count = Math.Min(frames - written, _blockSamples - first);
            _block.AsSpan(first, count).CopyTo(destination.Slice(written, count));
            written += count;
            _frame += count;
        }
        return written;
    }

    /// <inheritdoc/>
    public void Dispose()
        => _disposed = true;

    private static long MaxFrames(byte[] data, int blockSamples)
    {
        ArgumentNullException.ThrowIfNull(data);
        ImaAdpcm.ValidateBlockSamples(blockSamples, nameof(blockSamples));
        return ImaAdpcm.GetMaxFrames(data.Length, blockSamples);
    }

    private void DecodeBlock(long block)
    {
        long offset = block * _blockBytes;
        long codeBytes = Math.Min(_blockBytes, _data.Length - offset) - ImaAdpcm.HeaderBytes;
        int samples = (int)Math.Min(_blockSamples, 1 + codeBytes * 2);

        int predictor = (short)(_data[offset] | (_data[offset + 1] << 8));
        int stepIndex = _data[offset + 2];
        _block[0] = predictor / 32768.0f;
        for (int i = 1; i < samples; i++)
        {
            byte packed = _data[offset + ImaAdpcm.HeaderBytes + ((i - 1) >> 1)];
            int code = ((i - 1) & 1) == 0 ? packed & 0x0F : packed >> 4;
            ImaAdpcm.Step(code, ref predictor, ref stepIndex);
            _block[i] = predictor / 32768.0f;
        }
        _decodedBlock = block;
    }
}
