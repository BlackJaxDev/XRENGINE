using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.Components;

public partial class PhysicsChainComponent : IPhysicsChainComputeSource
{
    IPhysicsChainReadbackCoordinator? IPhysicsChainComputeSource.ReadbackCoordinator
    {
        get
        {
            IRuntimeWorldContext? world = World;
            return world is not null
                && PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? coordinator)
                    ? coordinator
                    : null;
        }
    }

    int IPhysicsChainComputeSource.UpdateMode => (int)UpdateMode;
    bool IPhysicsChainComputeSource.HasGpuDrivenRenderers => HasGpuDrivenRenderers;
    int IPhysicsChainComputeSource.GpuDebugInterpolationMode => (int)InterpolationMode;

    float IPhysicsChainComputeSource.GetGpuDebugInterpolationAlpha()
        => GetGpuDebugInterpolationAlpha();

    bool IPhysicsChainComputeSource.RequiresGpuReadback()
        => RequiresGpuReadback();

    void IPhysicsChainComputeSource.NotifyGpuReadbackUnavailable(string reason)
        => NotifyGpuReadbackUnavailable(reason);

    void IPhysicsChainComputeSource.ApplyReadbackData(
        ReadOnlySpan<GPUPhysicsChainDispatcher.GPUParticleData> readbackData,
        int generation,
        long submissionId)
        => ApplyReadbackData(readbackData, generation, submissionId);

    void IPhysicsChainComputeSource.AppendBatchedGpuDrivenBonePaletteBindings(
        int particleBaseOffset,
        List<GPUPhysicsChainDispatcher.GpuDrivenRendererPaletteBinding> bindings)
        => AppendBatchedGpuDrivenBonePaletteBindings(particleBaseOffset, bindings);

    void IPhysicsChainComputeSource.ClearBatchedGpuDrivenBonePaletteSources()
        => ClearBatchedGpuDrivenBonePaletteSources();

    bool IPhysicsChainComputeSource.PublishGpuDrivenBoneMatrices(
        XRDataBuffer? particlesBuffer,
        XRDataBuffer? transformMatricesBuffer,
        int particleBaseOffset,
        bool includeCompletePalettes,
        IPhysicsChainComputeBackend? backend)
        => PublishGpuDrivenBoneMatrices(
            particlesBuffer,
            transformMatricesBuffer,
            particleBaseOffset,
            includeCompletePalettes,
            backend);
}

public sealed partial class PhysicsChainWorld : IPhysicsChainReadbackCoordinator
{
    PhysicsChainReadbackTransferCounters IPhysicsChainReadbackCoordinator.GetReadbackTransferCounters()
        => this.GetReadbackTransferCounters();

    int IPhysicsChainReadbackCoordinator.BuildPendingReadbackGatherPlans(
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        Span<PhysicsChainReadbackGatherPlan?> destination)
        => this.BuildPendingReadbackGatherPlans(sourceEpoch, gatherFrame, destination);

    bool IPhysicsChainReadbackCoordinator.TryAcquireReadbackStagingSlot(
        PhysicsChainReadbackGatherPlan plan,
        out PhysicsChainReadbackStagingLease lease,
        out PhysicsChainReadbackTransferFailure failure)
        => this.TryAcquireReadbackStagingSlot(plan, out lease, out failure);

    bool IPhysicsChainReadbackCoordinator.FailReadbackStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        long completionFrame)
        => this.FailReadbackStagingSlot(lease, completionFrame);

    bool IPhysicsChainReadbackCoordinator.CommitReadbackStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        IPhysicsChainReadbackStagingSource source,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
        => this.CommitReadbackStagingSlot(lease, source, fence, transferFrame, out failure);

    void IPhysicsChainReadbackCoordinator.PollReadbackTransfers(
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch)
        => this.PollReadbackTransfers(currentFrame, currentEpoch);
}
