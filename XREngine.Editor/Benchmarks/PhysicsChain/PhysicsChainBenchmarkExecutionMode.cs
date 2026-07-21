namespace XREngine.Editor.Benchmarks.PhysicsChain;

public enum PhysicsChainBenchmarkExecutionMode : byte
{
    CpuStrict,
    GpuStrictZeroReadback,
    CpuQualityTiered,
    GpuQualityTiered,
}
