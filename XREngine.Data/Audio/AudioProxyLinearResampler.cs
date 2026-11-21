namespace XREngine.Data;

/// <summary>
/// A streaming linear resampler for the proxy bake. Output frame <c>i</c> is at source position
/// <c>i * sourceRate / targetRate</c>, which it computes with integers, so the result is deterministic and does not drift
/// on long files. Between two source frames it interpolates on a straight line, and after the last source frame it holds
/// that frame. The output has <c>ceil(n * targetRate / sourceRate)</c> frames for <c>n</c> source frames.
/// </summary>
internal sealed class AudioProxyLinearResampler
{
    // A wrong, very large frame count of a decoder must not make a huge first buffer. The buffer grows when it is full.
    private const int MaxInitialCapacity = 1 << 22;

    private readonly int _sourceRate;
    private readonly int _targetRate;
    private float[] _output;
    private int _outputCount;
    private long _baseFrame;
    private float _previous;
    private long _next;

    /// <summary>Creates a resampler.</summary>
    /// <param name="sourceRate">The input sample rate.</param>
    /// <param name="targetRate">The output sample rate.</param>
    /// <param name="expectedSourceFrames">The expected number of input frames, or zero if it is not known.</param>
    public AudioProxyLinearResampler(int sourceRate, int targetRate, long expectedSourceFrames)
    {
        _sourceRate = sourceRate;
        _targetRate = targetRate;
        long expected = expectedSourceFrames > 0 ? CeilDiv(expectedSourceFrames * targetRate, sourceRate) : 0;
        _output = new float[(int)Math.Clamp(expected, 16, MaxInitialCapacity)];
    }

    /// <summary>Adds the next input frames.</summary>
    /// <param name="input">The next mono input frames.</param>
    public void Push(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty)
            return;

        long end = _baseFrame + input.Length;

        // Output frame i needs source frames idx and idx + 1. Write each frame whose two source frames are known.
        while (true)
        {
            long position = _next * _sourceRate;
            long index = position / _targetRate;
            if (index + 1 >= end)
                break;

            float first = index < _baseFrame ? _previous : input[(int)(index - _baseFrame)];
            float second = input[(int)(index + 1 - _baseFrame)];
            float fraction = (float)(position - index * _targetRate) / _targetRate;
            Add(first + (second - first) * fraction);
            _next++;
        }

        _previous = input[^1];
        _baseFrame = end;
    }

    /// <summary>Writes the last frames, which hold the last input frame, and gives the output.</summary>
    /// <returns>The output frames. The array has the exact length.</returns>
    public float[] Finish()
    {
        long total = CeilDiv(_baseFrame * _targetRate, _sourceRate);
        for (; _next < total; _next++)
            Add(_previous);

        if (_output.Length != _outputCount)
            Array.Resize(ref _output, _outputCount);
        return _output;
    }

    private void Add(float value)
    {
        if (_outputCount == _output.Length)
            Array.Resize(ref _output, _output.Length * 2);
        _output[_outputCount++] = value;
    }

    private static long CeilDiv(long value, long divisor)
        => (value + divisor - 1) / divisor;
}
