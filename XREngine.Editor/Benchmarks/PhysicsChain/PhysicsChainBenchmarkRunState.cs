namespace XREngine.Editor.Benchmarks.PhysicsChain;

public enum PhysicsChainBenchmarkRunState : byte
{
    Settling,
    Measuring,
    Complete,
    SettleTimedOut,
}
