namespace XREngine.Editor;

/// <summary>
/// Keeps a fixed number of render-stat samples for each <see cref="ENativeProfilerStatSeries"/>.
/// All series advance together, one sample for each render frame. The storage is allocated once.
/// </summary>
internal sealed class NativeProfilerStatHistory
{
    public const int Capacity = NativeProfilerRootHistory.Capacity;
    private const int SeriesCount = (int)ENativeProfilerStatSeries.Count;
    private readonly float[] _samples = new float[SeriesCount * Capacity];
    private readonly float[] _pending = new float[SeriesCount];
    private int _count;
    private int _next;
    private ulong _lastFrameId;
    private bool _hasFrame;

    /// <summary>Changes each time a frame is committed or the history is cleared.</summary>
    public int Revision { get; private set; }

    /// <summary>Gets the number of committed frames, up to <see cref="Capacity"/>.</summary>
    public int Count => _count;

    /// <summary>Sets the value of one series for the frame that the next <see cref="TryCommit"/> adds.</summary>
    public void Set(ENativeProfilerStatSeries series, double value)
    {
        int index = (int)series;
        if (index <= 0 || index >= SeriesCount)
            return;
        _pending[index] = double.IsFinite(value) ? (float)value : 0f;
    }

    /// <summary>
    /// Adds the pending values as one frame. A second call for the same render frame does nothing,
    /// so a panel that ticks faster than the renderer does not add duplicate samples.
    /// </summary>
    /// <returns><see langword="true"/> when a new frame was added.</returns>
    public bool TryCommit(ulong frameId)
    {
        if (_hasFrame && frameId == _lastFrameId)
            return false;
        _hasFrame = true;
        _lastFrameId = frameId;
        for (int series = 1; series < SeriesCount; series++)
            _samples[series * Capacity + _next] = _pending[series];
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        Revision++;
        return true;
    }

    /// <summary>Removes all samples. The next commit starts a new history.</summary>
    public void Clear()
    {
        _count = 0;
        _next = 0;
        _hasFrame = false;
        Array.Clear(_pending);
        Revision++;
    }

    /// <summary>Copies the newest samples of one series, oldest first.</summary>
    /// <returns>The number of samples copied.</returns>
    public int CopyLatest(ENativeProfilerStatSeries series, Span<float> destination, int requested)
    {
        int index = (int)series;
        if (index <= 0 || index >= SeriesCount)
            return 0;
        int copied = Math.Min(Math.Min(_count, destination.Length), Math.Clamp(requested, 0, Capacity));
        int first = (_next - copied + Capacity) % Capacity;
        int offset = index * Capacity;
        for (int sample = 0; sample < copied; sample++)
            destination[sample] = _samples[offset + (first + sample) % Capacity];
        return copied;
    }

    /// <summary>Gets the newest sample, the average, and the maximum of one series over the newest frames.</summary>
    /// <returns><see langword="false"/> when the history has no samples.</returns>
    public bool TryGetSummary(ENativeProfilerStatSeries series, int requested,
        out float latest, out float average, out float maximum)
    {
        latest = average = maximum = 0f;
        int index = (int)series;
        int count = Math.Min(_count, Math.Clamp(requested, 1, Capacity));
        if (index <= 0 || index >= SeriesCount || _count == 0)
            return false;
        int first = (_next - count + Capacity) % Capacity;
        int offset = index * Capacity;
        float sum = 0f;
        for (int sample = 0; sample < count; sample++)
        {
            float value = _samples[offset + (first + sample) % Capacity];
            sum += value;
            maximum = Math.Max(maximum, value);
        }
        latest = _samples[offset + (_next - 1 + Capacity) % Capacity];
        average = sum / count;
        return true;
    }

    /// <summary>Gets the fixed top of the plot scale for a series, or zero to scale to the data.</summary>
    public static float FixedMaximum(ENativeProfilerStatSeries series)
        => series is ENativeProfilerStatSeries.VulkanPipelineSkipPercent
            or ENativeProfilerStatSeries.VulkanDescriptorSkipPercent
            or ENativeProfilerStatSeries.VulkanPipelineCacheHitPercent ? 100f : 0f;
}
