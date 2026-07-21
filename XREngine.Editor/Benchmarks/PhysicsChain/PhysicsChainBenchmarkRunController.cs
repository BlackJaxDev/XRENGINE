namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Pure state machine that keeps warmup/settling outside the timed sample set
/// and requires both the duration and sample-count contracts before completion.
/// </summary>
public sealed class PhysicsChainBenchmarkRunController
{
    public const int DefaultMaximumSettleFrameCount = 600;

    private readonly PhysicsChainBenchmarkConfiguration _configuration;
    private readonly PhysicsChainBenchmarkSettleGate _settleGate;
    private readonly int _maximumSettleFrameCount;
    private int _settleFrameCount;

    public PhysicsChainBenchmarkRunController(
        PhysicsChainBenchmarkConfiguration configuration,
        int maximumSettleFrameCount = DefaultMaximumSettleFrameCount)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSettleFrameCount, configuration.StableFrameCount);

        _configuration = configuration;
        _maximumSettleFrameCount = maximumSettleFrameCount;
        _settleGate = new PhysicsChainBenchmarkSettleGate(configuration.StableFrameCount);
    }

    public PhysicsChainBenchmarkRunState State { get; private set; } = PhysicsChainBenchmarkRunState.Settling;
    public int SettleFrameCount => _settleFrameCount;
    public int StableFrameCount => _settleGate.StableFrameCount;

    public PhysicsChainBenchmarkRunState ObserveSettleFrame(in PhysicsChainBenchmarkSettleSnapshot snapshot)
    {
        if (State != PhysicsChainBenchmarkRunState.Settling)
            return State;

        ++_settleFrameCount;
        if (_settleGate.Observe(snapshot))
            State = PhysicsChainBenchmarkRunState.Measuring;
        else if (_settleFrameCount >= _maximumSettleFrameCount)
            State = PhysicsChainBenchmarkRunState.SettleTimedOut;

        return State;
    }

    public PhysicsChainBenchmarkRunState ObserveMeasurement(double elapsedSeconds, int sampleFrameCount)
    {
        if (State != PhysicsChainBenchmarkRunState.Measuring)
            return State;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        ArgumentOutOfRangeException.ThrowIfNegative(sampleFrameCount);

        if (elapsedSeconds >= _configuration.MinimumDurationSeconds
            && sampleFrameCount >= _configuration.MinimumSampleFrameCount)
        {
            State = PhysicsChainBenchmarkRunState.Complete;
        }

        return State;
    }

    public void Reset()
    {
        _settleFrameCount = 0;
        _settleGate.Reset();
        State = PhysicsChainBenchmarkRunState.Settling;
    }
}
