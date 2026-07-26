using XREngine.Components;

namespace XREngine.Rendering.Compute;

/// <summary>
/// CPU-world selective-readback surface consumed by the render-thread dispatcher.
/// </summary>
public interface IPhysicsChainReadbackCoordinator
{
    PhysicsChainReadbackTransferCounters GetReadbackTransferCounters();
    int BuildPendingReadbackGatherPlans(
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        Span<PhysicsChainReadbackGatherPlan?> destination);
    bool TryAcquireReadbackStagingSlot(
        PhysicsChainReadbackGatherPlan plan,
        out PhysicsChainReadbackStagingLease lease,
        out PhysicsChainReadbackTransferFailure failure);
    bool FailReadbackStagingSlot(PhysicsChainReadbackStagingLease lease, long completionFrame);
    bool CommitReadbackStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        IPhysicsChainReadbackStagingSource source,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure);
    void PollReadbackTransfers(
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch);
}
