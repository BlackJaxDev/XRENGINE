using System.Runtime.CompilerServices;

namespace XREngine.Components;

/// <summary>Lower readback lifecycle operations shared with renderer-owned transfer adapters.</summary>
public static class PhysicsChainWorldReadbackExtensions
{
    private static readonly ConditionalWeakTable<PhysicsChainWorld, PhysicsChainReadbackService> Services = [];

    public static bool TryRequestReadback(
        this PhysicsChainWorld world,
        PhysicsChainRuntimeHandle instanceHandle,
        PhysicsChainReadbackFields fields,
        ReadOnlySpan<int> selectedElementIndices,
        int expectedByteCount,
        long submissionFrame,
        out PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackRejection rejection)
    {
        PhysicsChainReadbackService service = GetService(world);
        bool sourceValid = world.TryCaptureReadbackSource(instanceHandle, out PhysicsChainComponent? component,
            out long sourceGeneration, out PhysicsChainReadbackRejection sourceRejection);
        using var scope = service.SyncRoot.EnterScope();
        if (!sourceValid)
        {
            handle = PhysicsChainReadbackHandle.Invalid;
            rejection = sourceRejection;
            service.RecordRejectedRequest();
            return false;
        }

        return service.TryRequest(
            instanceHandle,
            fields,
            selectedElementIndices,
            expectedByteCount,
            submissionFrame,
            out handle,
            out rejection,
            component,
            sourceGeneration);
    }

    public static bool TryGetReadbackRequest(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackRequestInfo? info)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.TryGet(handle, out info);
    }

    public static bool CancelReadback(this PhysicsChainWorld world, PhysicsChainReadbackHandle handle)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.Cancel(handle);
    }

    public static void AdvanceReadbacks(this PhysicsChainWorld world, long currentFrame)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        service.AdvanceFrame(currentFrame);
    }

    public static bool ReleaseReadback(this PhysicsChainWorld world, PhysicsChainReadbackHandle handle)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.Release(handle);
    }

    public static PhysicsChainReadbackCounters GetReadbackCounters(this PhysicsChainWorld world)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.GetCounters();
    }

    public static bool TryBuildReadbackGatherPlan(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        out PhysicsChainReadbackGatherPlan? plan,
        out PhysicsChainReadbackTransferFailure failure)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.TryBuildGatherPlan(handle, sourceEpoch, gatherFrame, out plan, out failure);
    }

    public static int BuildPendingReadbackGatherPlans(
        this PhysicsChainWorld world,
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        Span<PhysicsChainReadbackGatherPlan?> destination)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.BuildPendingGatherPlans(sourceEpoch, gatherFrame, destination);
    }

    public static bool TryAcquireReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackGatherPlan plan,
        out PhysicsChainReadbackStagingLease lease,
        out PhysicsChainReadbackTransferFailure failure)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.TryAcquireStagingSlot(plan, out lease, out failure);
    }

    /// <summary>
    /// Managed CPU/testing convenience path. GPU backends should pass an
    /// <see cref="IPhysicsChainReadbackStagingSource"/> instead.
    /// </summary>
    public static bool CommitReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease,
        ReadOnlySpan<byte> packedData,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.CommitStagingSlot(lease, packedData, fence, transferFrame, out failure);
    }

    public static bool CommitReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease,
        IPhysicsChainReadbackStagingSource source,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.CommitStagingSlot(
            lease,
            source,
            fence,
            transferFrame,
            out failure);
    }

    public static bool AbandonReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.AbandonStagingSlot(lease);
    }

    public static bool FailReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease,
        long completionFrame)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.FailStagingSlot(lease, completionFrame);
    }

    public static void PollReadbackTransfers(
        this PhysicsChainWorld world,
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        service.AdvanceFrame(currentFrame);
        service.PollTransfers(world, currentFrame, currentEpoch);
    }

    public static bool TryGetReadbackResult(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackResult? result)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.TryGetResult(handle, out result);
    }

    public static PhysicsChainReadbackTransferCounters GetReadbackTransferCounters(
        this PhysicsChainWorld world)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.GetTransferCounters();
    }

    public static bool HasPendingReadbackTransfers(this PhysicsChainWorld world)
    {
        PhysicsChainReadbackService service = GetService(world);
        using var scope = service.SyncRoot.EnterScope();
        return service.HasPendingTransfers();
    }

    private static PhysicsChainReadbackService GetService(PhysicsChainWorld world)
        => Services.GetValue(world, static _ => new PhysicsChainReadbackService(PhysicsChainReadbackLimits.Default));
}
