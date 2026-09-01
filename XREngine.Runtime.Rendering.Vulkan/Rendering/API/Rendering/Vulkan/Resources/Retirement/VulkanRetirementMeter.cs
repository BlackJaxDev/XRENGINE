using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Per-resource-runtime, per-production-frame retirement budget.  Admission is
/// deliberately after the caller's completion proof so emergency backlog work
/// can never bypass Vulkan lifetime readiness.
/// </summary>
internal sealed class VulkanRetirementMeter
{
    private const int ClassCount = (int)EVulkanRetirementWorkClass.Callback + 1;
    // Fixed one-microsecond buckets remain comparable across Stopwatch
    // frequencies. The final bucket is an explicit overflow sentinel.
    private const int DrainDurationHistogramBins = 2_049;
    private const int DrainDurationOverflowBin = DrainDurationHistogramBins - 1;
    private readonly int[] _ordinaryCaps = new int[ClassCount];
    private readonly int[] _highWaterMarks = new int[ClassCount];
    private readonly int[] _admitted = new int[ClassCount];
    private readonly int[] _completed = new int[ClassCount];
    private readonly int[] _scanned = new int[ClassCount];
    private readonly Dictionary<object, ScanState> _scanStates = new(ReferenceEqualityComparer.Instance);
    private readonly int[] _deferred = new int[ClassCount];
    private readonly int[] _backlog = new int[ClassCount];
    private readonly bool[] _uncapped = new bool[ClassCount];
    private readonly int[] _uncappedActivations = new int[ClassCount];
    private readonly double[] _oldestPendingAgeMilliseconds = new double[ClassCount];
    private readonly long[] _drainDurationHistogram = new long[DrainDurationHistogramBins];
    private int _forcedBudgetBypassDepth;
    private long _frameSerial;
    private long _drainElapsedTicks;
    private long _currentFrameDrainTicks;
    private long _activeDurationFrameSerial = long.MinValue;
    private long _publishedDrainSampleCount;
    private long _drainDurationOverflowCount;
    private long _maximumPublishedDrainDurationTicks;

    internal VulkanRetirementMeter()
    {
        for (int index = 0; index < ClassCount; index++)
        {
            _ordinaryCaps[index] = 8;
            _highWaterMarks[index] = 64;
        }

        Configure(EVulkanRetirementWorkClass.Image, 8);
        Configure(EVulkanRetirementWorkClass.ImageView, 32);
        Configure(EVulkanRetirementWorkClass.Sampler, 8);
        Configure(EVulkanRetirementWorkClass.Buffer, 64);
        Configure(EVulkanRetirementWorkClass.Pipeline, 8);
        Configure(EVulkanRetirementWorkClass.PipelineLayout, 8);
        Configure(EVulkanRetirementWorkClass.Descriptor, 64);
        Configure(EVulkanRetirementWorkClass.QueryPool, 32);
        Configure(EVulkanRetirementWorkClass.Framebuffer, 32);
        Configure(EVulkanRetirementWorkClass.CommandArtifact, 64);
        Configure(EVulkanRetirementWorkClass.Callback, 16);
    }

    internal void Configure(EVulkanRetirementWorkClass workClass, int ordinaryCap, int? highWaterMark = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinaryCap);
        int index = (int)workClass;
        _ordinaryCaps[index] = ordinaryCap;
        _highWaterMarks[index] = highWaterMark ?? checked(ordinaryCap * 8);
    }

    /// <summary>Starts a new production-frame accounting interval.</summary>
    internal void BeginFrame(long frameSerial)
    {
        if (frameSerial == Volatile.Read(ref _frameSerial))
            return;

        if (_activeDurationFrameSerial != long.MinValue)
            PublishCurrentFrameDrainDuration();

        Array.Clear(_admitted);
        Array.Clear(_completed);
        Array.Clear(_scanned);
        Array.Clear(_deferred);
        Array.Clear(_backlog);
        Array.Clear(_uncapped);
        Array.Clear(_oldestPendingAgeMilliseconds);
        foreach (ScanState state in _scanStates.Values)
        {
            state.FrameSerial = frameSerial;
            state.Scanned = 0;
        }
        Volatile.Write(ref _frameSerial, frameSerial);
        Interlocked.Exchange(ref _drainElapsedTicks, 0);
        Interlocked.Exchange(ref _currentFrameDrainTicks, 0);
        Volatile.Write(ref _activeDurationFrameSerial, frameSerial);
    }

    /// <summary>
    /// Reserves the complete native-destruction cost of one completion-proven
    /// entry. A single oversized entry is admitted when the class is otherwise
    /// idle so an atomic image bundle can never starve forever under its normal
    /// cap. This safety admission is reported like a high-water uncapped drain.
    /// </summary>
    internal bool TryAdmit(EVulkanRetirementWorkClass workClass, int cost, int backlog)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);
        int index = (int)workClass;
        _backlog[index] = Math.Max(_backlog[index], backlog);
        if (Volatile.Read(ref _forcedBudgetBypassDepth) > 0 || backlog >= _highWaterMarks[index])
        {
            if (Volatile.Read(ref _forcedBudgetBypassDepth) == 0)
                MarkUncapped(index);
            _admitted[index] += cost;
            return true;
        }

        int used = _admitted[index];
        if (used == 0 || used <= _ordinaryCaps[index] - cost)
        {
            if (used == 0 && cost > _ordinaryCaps[index])
                MarkUncapped(index);
            _admitted[index] = used + cost;
            return true;
        }

        _deferred[index]++;
        return false;
    }

    /// <summary>
    /// Atomically admits a ready image bundle, including every view and sampler
    /// it destroys. An otherwise-idle class may admit one oversized bundle and
    /// records that safety exception as an uncapped activation.
    /// </summary>
    internal bool TryAdmitImageBundle(int images, int views, int samplers, int backlog)
    {
        if (!CanAdmit(EVulkanRetirementWorkClass.Image, images, backlog) ||
            !CanAdmit(EVulkanRetirementWorkClass.ImageView, views, backlog) ||
            !CanAdmit(EVulkanRetirementWorkClass.Sampler, samplers, backlog))
        {
            _deferred[(int)EVulkanRetirementWorkClass.Image]++;
            return false;
        }

        Admit(EVulkanRetirementWorkClass.Image, images, backlog);
        Admit(EVulkanRetirementWorkClass.ImageView, views, backlog);
        Admit(EVulkanRetirementWorkClass.Sampler, samplers, backlog);
        return true;
    }

    private bool CanAdmit(EVulkanRetirementWorkClass workClass, int cost, int backlog)
    {
        if (cost == 0 || Volatile.Read(ref _forcedBudgetBypassDepth) > 0 || backlog >= _highWaterMarks[(int)workClass])
            return true;
        int index = (int)workClass;
        int used = _admitted[index];
        return used == 0 || used <= _ordinaryCaps[index] - cost;
    }

    private void Admit(EVulkanRetirementWorkClass workClass, int cost, int backlog)
    {
        int index = (int)workClass;
        _backlog[index] = Math.Max(_backlog[index], backlog);
        if (backlog >= _highWaterMarks[index] ||
            (_admitted[index] == 0 && cost > _ordinaryCaps[index]))
        {
            MarkUncapped(index);
        }
        _admitted[index] += cost;
    }

    private void MarkUncapped(int index)
    {
        if (!_uncapped[index])
            _uncappedActivations[index]++;
        _uncapped[index] = true;
    }

    /// <summary>Records queue state when a drain yields due to readiness or budget.</summary>
    internal void ReportBacklog(EVulkanRetirementWorkClass workClass, int backlog, int deferred)
    {
        int index = (int)workClass;
        _backlog[index] = Math.Max(_backlog[index], backlog);
        _deferred[index] += deferred;
    }

    /// <summary>Registers a stable retirement list so every slot receives a bounded rotating scan share.</summary>
    internal void RegisterScanQueue(EVulkanRetirementWorkClass workClass, object queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (_scanStates.ContainsKey(queue))
            return;
        _scanStates.Add(queue, new ScanState(workClass, GetRegisteredQueueCount(workClass)));
    }

    /// <summary>
    /// Grants only this queue's unused fair scan share. The grant is not charged
    /// until <see cref="CompleteScan"/> reports entries actually inspected.
    /// </summary>
    internal int ReserveScanLimit(EVulkanRetirementWorkClass workClass, object queue, int requested)
    {
        if (requested <= 0)
            return 0;
        if (!_scanStates.TryGetValue(queue, out ScanState? state) || state.WorkClass != workClass)
            throw new InvalidOperationException("The retirement queue was not registered for this work class.");
        if (Volatile.Read(ref _forcedBudgetBypassDepth) > 0)
            return requested;
        int index = (int)workClass;
        int allowed = Math.Max(8, _ordinaryCaps[index] * 4);
        int queues = GetRegisteredQueueCount(workClass);
        int share = allowed / queues;
        int remainder = allowed % queues;
        if (state.Ordinal < remainder)
            share++;
        int remaining = share - state.Scanned;
        if (remaining <= 0)
            return 0;
        return Math.Min(requested, remaining);
    }

    internal int GetRotatingScanStart(object queue, int count)
    {
        if (count <= 0)
            return 0;
        if (!_scanStates.TryGetValue(queue, out ScanState? state))
            throw new InvalidOperationException("The retirement queue was not registered.");
        return (int)((uint)state.Cursor % (uint)count);
    }

    internal void CompleteScan(EVulkanRetirementWorkClass workClass, object queue, int inspected, int nextIndex, int remainingCount)
    {
        if (!_scanStates.TryGetValue(queue, out ScanState? state) || state.WorkClass != workClass)
            throw new InvalidOperationException("The retirement queue was not registered for this work class.");
        if (inspected < 0)
            throw new ArgumentOutOfRangeException(nameof(inspected));
        state.Scanned += inspected;
        _scanned[(int)workClass] += inspected;
        state.Cursor = remainingCount == 0 ? 0 : (int)((uint)nextIndex % (uint)remainingCount);
    }

    internal void RecordCompleted(EVulkanRetirementWorkClass workClass, int cost = 1)
        => _completed[(int)workClass] += cost;

    /// <summary>Explicit shutdown-only budget bypass, independent of lifetime readiness policy.</summary>
    internal BudgetBypassScope EnterForcedBudgetBypass()
    {
        Interlocked.Increment(ref _forcedBudgetBypassDepth);
        return new(this);
    }

    internal VulkanRetirementMeterSnapshot GetSnapshot()
        => new(this, Volatile.Read(ref _frameSerial));

    internal int GetAdmitted(EVulkanRetirementWorkClass workClass) => _admitted[(int)workClass];
    internal int GetCompleted(EVulkanRetirementWorkClass workClass) => _completed[(int)workClass];
    internal int GetOrdinaryCap(EVulkanRetirementWorkClass workClass) => _ordinaryCaps[(int)workClass];
    internal int GetHighWaterMark(EVulkanRetirementWorkClass workClass) => _highWaterMarks[(int)workClass];
    internal int GetDeferred(EVulkanRetirementWorkClass workClass) => _deferred[(int)workClass];
    internal int GetBacklog(EVulkanRetirementWorkClass workClass) => _backlog[(int)workClass];
    internal bool IsUncapped(EVulkanRetirementWorkClass workClass) => _uncapped[(int)workClass];
    internal int GetUncappedActivationCount(EVulkanRetirementWorkClass workClass) => _uncappedActivations[(int)workClass];
    internal double GetElapsedMilliseconds()
        => Interlocked.Read(ref _drainElapsedTicks) * 1000.0 / Stopwatch.Frequency;
    internal long GetDrainDurationSampleCount() => Interlocked.Read(ref _publishedDrainSampleCount);
    internal long GetDrainDurationOverflowCount() => Interlocked.Read(ref _drainDurationOverflowCount);
    internal double GetMaximumPublishedDrainDurationMilliseconds()
        => Interlocked.Read(ref _maximumPublishedDrainDurationTicks) * 1000.0 / Stopwatch.Frequency;
    internal double GetDrainDurationPercentileMilliseconds(double percentile)
    {
        if (percentile is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        long sampleCount = Interlocked.Read(ref _publishedDrainSampleCount);
        if (sampleCount == 0)
            return 0.0;
        long target = checked((long)Math.Ceiling(sampleCount * percentile));
        target = Math.Max(target, 1);
        long accumulated = 0;
        for (int index = 0; index < _drainDurationHistogram.Length; index++)
        {
            accumulated += Interlocked.Read(ref _drainDurationHistogram[index]);
            if (accumulated >= target)
                return index == DrainDurationOverflowBin
                    ? GetMaximumPublishedDrainDurationMilliseconds()
                    : index / 1000.0;
        }
        return GetMaximumPublishedDrainDurationMilliseconds();
    }
    internal DrainTimingScope MeasureDrain() => new(this, Stopwatch.GetTimestamp());
    internal void RecordOldestPendingTimestamp(EVulkanRetirementWorkClass workClass, long timestamp)
    {
        if (timestamp == 0)
            return;
        int index = (int)workClass;
        _oldestPendingAgeMilliseconds[index] = Math.Max(
            _oldestPendingAgeMilliseconds[index],
            (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency);
    }
    internal double GetOldestPendingAgeMilliseconds(EVulkanRetirementWorkClass workClass)
        => _oldestPendingAgeMilliseconds[(int)workClass];

    internal readonly struct BudgetBypassScope(VulkanRetirementMeter owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._forcedBudgetBypassDepth);
    }

    internal readonly struct DrainTimingScope(VulkanRetirementMeter owner, long startedTimestamp) : IDisposable
    {
        public void Dispose()
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
            Interlocked.Add(ref owner._drainElapsedTicks, elapsedTicks);
            Interlocked.Add(ref owner._currentFrameDrainTicks, elapsedTicks);
        }
    }

    private void PublishCurrentFrameDrainDuration()
    {
        long durationTicks = Interlocked.Read(ref _currentFrameDrainTicks);
        long durationMicroseconds = checked((durationTicks * 1_000_000L + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
        int bin = (int)Math.Min(durationMicroseconds, DrainDurationOverflowBin);
        Interlocked.Increment(ref _drainDurationHistogram[bin]);
        if (bin == DrainDurationOverflowBin)
            Interlocked.Increment(ref _drainDurationOverflowCount);
        RecordMaximumPublishedDrainDuration(durationTicks);
        Interlocked.Increment(ref _publishedDrainSampleCount);
    }

    private void RecordMaximumPublishedDrainDuration(long durationTicks)
    {
        long observed;
        while (durationTicks > (observed = Interlocked.Read(ref _maximumPublishedDrainDurationTicks)))
            if (Interlocked.CompareExchange(ref _maximumPublishedDrainDurationTicks, durationTicks, observed) == observed)
                return;
    }

    private int GetRegisteredQueueCount(EVulkanRetirementWorkClass workClass)
    {
        int count = 0;
        foreach (ScanState state in _scanStates.Values)
            if (state.WorkClass == workClass)
                count++;
        return count;
    }

    private sealed class ScanState(EVulkanRetirementWorkClass workClass, int ordinal)
    {
        internal EVulkanRetirementWorkClass WorkClass { get; } = workClass;
        internal int Ordinal { get; } = ordinal;
        internal long FrameSerial { get; set; } = long.MinValue;
        internal int Scanned { get; set; }
        internal int Cursor { get; set; }
    }
}
