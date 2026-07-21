using System.Runtime.CompilerServices;

namespace XREngine.Components;

internal static class PhysicsChainWorldReadbackExtensions
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
        if (!world.TryResolveRuntimeHandle(instanceHandle, out _))
        {
            handle = PhysicsChainReadbackHandle.Invalid;
            rejection = PhysicsChainReadbackRejection.InvalidInstance;
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
            out rejection);
    }

    public static bool TryGetReadbackRequest(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackRequestInfo? info)
        => GetService(world).TryGet(handle, out info);

    public static bool CancelReadback(this PhysicsChainWorld world, PhysicsChainReadbackHandle handle)
        => GetService(world).Cancel(handle);

    public static void AdvanceReadbacks(this PhysicsChainWorld world, long currentFrame)
        => GetService(world).AdvanceFrame(currentFrame);

    public static bool ReleaseReadback(this PhysicsChainWorld world, PhysicsChainReadbackHandle handle)
        => GetService(world).Release(handle);

    public static PhysicsChainReadbackCounters GetReadbackCounters(this PhysicsChainWorld world)
        => GetService(world).GetCounters();

    public static bool TryBuildReadbackGatherPlan(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        out PhysicsChainReadbackGatherPlan? plan,
        out PhysicsChainReadbackTransferFailure failure)
        => GetService(world).TryBuildGatherPlan(handle, sourceEpoch, gatherFrame, out plan, out failure);

    public static int BuildPendingReadbackGatherPlans(
        this PhysicsChainWorld world,
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        Span<PhysicsChainReadbackGatherPlan?> destination)
        => GetService(world).BuildPendingGatherPlans(sourceEpoch, gatherFrame, destination);

    public static bool TryAcquireReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackGatherPlan plan,
        out PhysicsChainReadbackStagingLease lease,
        out PhysicsChainReadbackTransferFailure failure)
        => GetService(world).TryAcquireStagingSlot(plan, out lease, out failure);

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
        => GetService(world).CommitStagingSlot(lease, packedData, fence, transferFrame, out failure);

    public static bool CommitReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease,
        IPhysicsChainReadbackStagingSource source,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
        => GetService(world).CommitStagingSlot(
            lease,
            source,
            fence,
            transferFrame,
            out failure);

    public static bool AbandonReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease)
        => GetService(world).AbandonStagingSlot(lease);

    public static bool FailReadbackStagingSlot(
        this PhysicsChainWorld world,
        PhysicsChainReadbackStagingLease lease,
        long completionFrame)
        => GetService(world).FailStagingSlot(lease, completionFrame);

    public static void PollReadbackTransfers(
        this PhysicsChainWorld world,
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch)
    {
        PhysicsChainReadbackService service = GetService(world);
        service.AdvanceFrame(currentFrame);
        service.PollTransfers(world, currentFrame, currentEpoch);
    }

    public static bool TryGetReadbackResult(
        this PhysicsChainWorld world,
        PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackResult? result)
        => GetService(world).TryGetResult(handle, out result);

    public static PhysicsChainReadbackTransferCounters GetReadbackTransferCounters(
        this PhysicsChainWorld world)
        => GetService(world).GetTransferCounters();

    private static PhysicsChainReadbackService GetService(PhysicsChainWorld world)
        => Services.GetValue(world, static _ => new PhysicsChainReadbackService(PhysicsChainReadbackLimits.Default));
}
