namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Opens only after an identical, ready resource snapshot has been observed
/// for the configured number of consecutive frames.
/// </summary>
public sealed class PhysicsChainBenchmarkSettleGate
{
    private readonly int _requiredStableFrames;
    private PhysicsChainBenchmarkSettleSnapshot _lastSnapshot;
    private bool _hasSnapshot;

    public PhysicsChainBenchmarkSettleGate(int requiredStableFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredStableFrames, 1);
        _requiredStableFrames = requiredStableFrames;
    }

    public int StableFrameCount { get; private set; }
    public bool IsSettled => StableFrameCount >= _requiredStableFrames;

    /// <summary>
    /// Observes one frame. Pending work and any resource-state change restart
    /// the consecutive-stability interval.
    /// </summary>
    public bool Observe(in PhysicsChainBenchmarkSettleSnapshot snapshot)
    {
        if (snapshot.HasPendingWork)
        {
            Reset();
            return false;
        }

        if (!_hasSnapshot || snapshot != _lastSnapshot)
        {
            _lastSnapshot = snapshot;
            _hasSnapshot = true;
            StableFrameCount = 1;
            return IsSettled;
        }

        if (!IsSettled)
            ++StableFrameCount;

        return IsSettled;
    }

    public void Reset()
    {
        _lastSnapshot = default;
        _hasSnapshot = false;
        StableFrameCount = 0;
    }
}
