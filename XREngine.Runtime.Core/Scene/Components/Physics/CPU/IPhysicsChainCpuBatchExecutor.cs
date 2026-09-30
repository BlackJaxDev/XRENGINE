namespace XREngine.Components;

/// <summary>Allocation-free coarse batch entry point used by persistent workers.</summary>
public interface IPhysicsChainCpuBatchExecutor
{
    bool TryStepBatch(ReadOnlySpan<PhysicsChainArenaHandle> handles);
}
