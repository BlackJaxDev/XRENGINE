namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Captures the state that must remain unchanged before benchmark timing may
/// begin. Signatures let a runner include resources that do not yet expose a
/// common arena or pipeline interface.
/// </summary>
public readonly record struct PhysicsChainBenchmarkSettleSnapshot(
    int ChainCount,
    long CapacitySignature,
    int PendingPipelineCompilationCount,
    int PendingUploadCount,
    int RendererCount)
{
    public bool HasPendingWork => PendingPipelineCompilationCount > 0 || PendingUploadCount > 0;
}
