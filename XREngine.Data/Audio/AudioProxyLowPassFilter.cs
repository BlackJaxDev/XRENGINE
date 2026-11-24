namespace XREngine.Data;

/// <summary>
/// A streaming, linear-phase low-pass FIR filter that runs before the proxy resampler when it lowers the sample rate.
/// It removes the content above the new Nyquist frequency, so that the decimation does not fold it into the audible band.
/// </summary>
/// <remarks>
/// <para>
/// The filter is a windowed sinc with a Hamming window. The cutoff is 0.45 times the target rate (0.9 times the target
/// Nyquist frequency). Each side has <c>16 * ceil(sourceRate / targetRate)</c> taps, so a 2:1 decimation uses 65 taps:
/// the response falls from about 8.8 kHz and reaches the stop band (about -50 dB) near 11 kHz. The coefficients come
/// from fixed double math and are scaled to a DC gain of one, so the output is deterministic.
/// </para>
/// <para>
/// The filter is centered, so it adds no delay: output <c>k</c> uses the inputs from <c>k - M</c> to <c>k + M</c>. It
/// holds the first and the last input outside the signal. Each call to <see cref="Process"/> gives as many outputs as it
/// gets inputs, except the first <c>M</c>, which <see cref="Flush"/> gives at the end.
/// </para>
/// </remarks>
internal sealed class AudioProxyLowPassFilter
{
    private const int TapsForEachRatioStep = 16;

    private readonly float[] _taps;
    private readonly int _half;
    private float[] _window;
    private int _windowCount;
    private long _inputCount;
    private long _nextOutput;

    /// <summary>Creates a filter for a decimation from one sample rate to a lower one.</summary>
    /// <param name="sourceRate">The input sample rate.</param>
    /// <param name="targetRate">The output sample rate of the resampler. It must be lower than the input rate.</param>
    /// <param name="maxInput">The largest number of inputs of one <see cref="Process"/> call.</param>
    public AudioProxyLowPassFilter(int sourceRate, int targetRate, int maxInput)
    {
        _half = TapsForEachRatioStep * (int)Math.Ceiling((double)sourceRate / targetRate);
        _taps = CreateTaps(_half, 0.45 * targetRate / sourceRate);
        _window = new float[2 * _half + maxInput];
        _nextOutput = -_half;
    }

    /// <summary>The number of taps on each side of the center tap.</summary>
    public int HalfLength => _half;

    /// <summary>The filter coefficients, from the first tap to the last.</summary>
    public ReadOnlySpan<float> Taps => _taps;

    /// <summary>Filters the next inputs.</summary>
    /// <param name="input">The next inputs. The count must not be more than the maximum of the constructor.</param>
    /// <param name="output">The span that receives the outputs. It must hold <paramref name="input"/>.Length values.</param>
    /// <returns>The number of outputs written.</returns>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.IsEmpty)
            return 0;

        // Before the first input, the window holds 2M copies of it: the hold of the first value.
        if (_inputCount == 0)
        {
            _window.AsSpan(0, 2 * _half).Fill(input[0]);
            _windowCount = 2 * _half;
        }

        input.CopyTo(_window.AsSpan(_windowCount));
        _windowCount += input.Length;
        _inputCount += input.Length;
        return Emit(input.Length, output);
    }

    /// <summary>Gives the last <see cref="HalfLength"/> outputs. Call it one time after the last input.</summary>
    /// <param name="output">The span that receives the outputs. It must hold <see cref="HalfLength"/> values.</param>
    /// <returns>The number of outputs written.</returns>
    public int Flush(Span<float> output)
    {
        if (_inputCount == 0)
            return 0;

        // After the last input, the window holds M copies of it: the hold of the last value.
        if (_window.Length < _windowCount + _half)
            Array.Resize(ref _window, _windowCount + _half);
        _window.AsSpan(_windowCount, _half).Fill(_window[_windowCount - 1]);
        _windowCount += _half;
        return Emit(_half, output);
    }

    // The window holds the inputs from (first output - M) on. Output t uses window items t to t + 2M.
    private int Emit(int count, Span<float> output)
    {
        int written = 0;
        for (int t = 0; t < count; t++, _nextOutput++)
        {
            // The first M outputs are before the start of the signal.
            if (_nextOutput < 0)
                continue;

            ReadOnlySpan<float> source = _window.AsSpan(t, _taps.Length);
            float sum = 0.0f;
            for (int j = 0; j < _taps.Length; j++)
                sum += _taps[j] * source[j];
            output[written++] = sum;
        }

        // Keep the last 2M items for the next call.
        _window.AsSpan(count, 2 * _half).CopyTo(_window);
        _windowCount = 2 * _half;
        return written;
    }

    private static float[] CreateTaps(int half, double cutoff)
    {
        int length = 2 * half + 1;
        double[] taps = new double[length];
        double sum = 0.0;
        for (int i = 0; i < length; i++)
        {
            int n = i - half;
            double sinc = n == 0 ? 2.0 * cutoff : Math.Sin(2.0 * Math.PI * cutoff * n) / (Math.PI * n);
            double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (length - 1));
            taps[i] = sinc * window;
            sum += taps[i];
        }

        float[] result = new float[length];
        for (int i = 0; i < length; i++)
            result[i] = (float)(taps[i] / sum);
        return result;
    }
}
