using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class RenderWorkAsynchronousBatchTests
{
    private const int BlockOperation = 40;
    private const int FaultOperation = 20;
    private const int NoOperation = 50;

    [Test]
    public void Submit_ReturnsWithoutWaitingForGatedItem_AndJoinReturnsResult()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = RentSingle(domain, executor, BlockOperation, lane: 1);

        domain.SubmitAsynchronous(lease, executor, frameSlot: 0);
        domain.IsAsynchronousComplete(lease).ShouldBeFalse();

        executor.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        executor.Release.Set();
        RenderWorkBatchResult result = domain.JoinAsynchronous(lease);

        result.Succeeded.ShouldBeTrue();
        domain.IsAsynchronousComplete(lease).ShouldBeTrue();
        executor.LaneMask.ShouldBe(1 << 1);
        lease.Dispose();
        domain.AsynchronousMetrics.OutstandingBatchCount.ShouldBe(0);
    }

    [TestCase(0)]
    [TestCase(RenderWorkItem.AnyLane)]
    [TestCase(3)]
    public void Submit_RejectsItemsThatDoNotNameABackgroundLane(int lane)
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = RentSingle(domain, executor, NoOperation, lane);

        Should.Throw<ArgumentException>(() => domain.SubmitAsynchronous(lease, executor, frameSlot: 0));

        // A rejected submit publishes nothing; the building rental can still be released.
        lease.Dispose();
        domain.AsynchronousMetrics.OutstandingBatchCount.ShouldBe(0);
        domain.Metrics.SubmittedBatchCount.ShouldBe(0);
    }

    [Test]
    public void SynchronousAndAsynchronousLeases_CannotCrossEntryPoints()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();

        RenderWorkBatchLease synchronous = domain.RentBatch(1);
        synchronous.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        Should.Throw<InvalidOperationException>(() => domain.SubmitAsynchronous(synchronous, executor, frameSlot: 0));
        synchronous.Dispose();

        RenderWorkBatchLease asynchronous = RentSingle(domain, executor, NoOperation, lane: 1);
        Should.Throw<InvalidOperationException>(() =>
        {
            RenderWorkBatchLease copy = asynchronous;
            domain.ExecuteAndWait(ref copy, executor);
        });
        asynchronous.Dispose();
    }

    [Test]
    public void Rental_EnforcesLimitAndOneRentalPerExecutor()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var first = new SchedulerTestRenderWorkExecutor();
        using var second = new SchedulerTestRenderWorkExecutor();
        using var third = new SchedulerTestRenderWorkExecutor();

        domain.TryRentAsynchronousBatch(first, 1, 0, out RenderWorkBatchLease firstLease).IsAdmitted.ShouldBeTrue();
        domain.TryRentAsynchronousBatch(first, 1, 0, out _).Reason
            .ShouldBe(ERenderWorkAdmissionReason.ExecutorBusy);
        domain.TryRentAsynchronousBatch(second, 1, 0, out RenderWorkBatchLease secondLease).IsAdmitted.ShouldBeTrue();
        domain.TryRentAsynchronousBatch(third, 1, 0, out _).Reason
            .ShouldBe(ERenderWorkAdmissionReason.AsynchronousCapacityExhausted);

        firstLease.Dispose();
        domain.TryRentAsynchronousBatch(third, 1, 0, out RenderWorkBatchLease thirdLease).IsAdmitted.ShouldBeTrue();
        secondLease.Dispose();
        thirdLease.Dispose();
        domain.AsynchronousMetrics.RejectedExecutorBusyCount.ShouldBe(1);
        domain.AsynchronousMetrics.RejectedCapacityExhaustedCount.ShouldBe(1);
    }

    [TestCase(1, 0)]
    [TestCase(2, 1)]
    [TestCase(8, 2)]
    public void Rental_LimitFollowsPoolCapacity(int poolCapacity, int expectedLimit)
    {
        using var domain = new RenderWorkDomain(
            backgroundWorkerCount: 1,
            ERenderWorkerQos.OsDefault,
            batchPoolCapacity: poolCapacity);
        domain.AsynchronousMetrics.AsynchronousRentalLimit.ShouldBe(expectedLimit);
        if (expectedLimit == 0)
        {
            using var executor = new SchedulerTestRenderWorkExecutor();
            domain.TryRentAsynchronousBatch(executor, 1, 0, out _).Reason
                .ShouldBe(ERenderWorkAdmissionReason.AsynchronousCapacityDisabled);
        }
    }

    [Test]
    public void Rental_NeverTakesTheLastFreeBatch()
    {
        using var domain = new RenderWorkDomain(
            backgroundWorkerCount: 1,
            ERenderWorkerQos.OsDefault,
            batchPoolCapacity: 3);
        using var first = new SchedulerTestRenderWorkExecutor();
        using var second = new SchedulerTestRenderWorkExecutor();

        RenderWorkBatchLease synchronous = domain.RentBatch(1);
        domain.TryRentAsynchronousBatch(first, 1, 0, out RenderWorkBatchLease asynchronous).IsAdmitted.ShouldBeTrue();
        domain.TryRentAsynchronousBatch(second, 1, 0, out _).Reason
            .ShouldBe(ERenderWorkAdmissionReason.LastFreeBatch);

        // Synchronous work can still use the remaining block.
        RenderWorkBatchLease lastSynchronous = domain.RentBatch(1);
        lastSynchronous.Dispose();
        synchronous.Dispose();
        asynchronous.Dispose();
    }

    [Test]
    public void Lifecycle_RejectsOutOfOrderCallsAndRepeatsTheJoinResult()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = RentSingle(domain, executor, BlockOperation, lane: 1);

        Should.Throw<InvalidOperationException>(() => domain.IsAsynchronousComplete(lease));
        Should.Throw<InvalidOperationException>(() => domain.JoinAsynchronous(lease));

        domain.SubmitAsynchronous(lease, executor, frameSlot: 0);
        Should.Throw<InvalidOperationException>(() => domain.SubmitAsynchronous(lease, executor, frameSlot: 0));

        // Disposing a submitted lease before its join is rejected and does not cancel it.
        Should.Throw<InvalidOperationException>(() => lease.Dispose());

        executor.Release.Set();
        RenderWorkBatchResult first = domain.JoinAsynchronous(lease);
        RenderWorkBatchResult second = domain.JoinAsynchronous(lease);
        first.Succeeded.ShouldBeTrue();
        second.ShouldBe(first);
        domain.AsynchronousMetrics.JoinCount.ShouldBe(1);

        // Cancel after the join changes nothing.
        domain.CancelAsynchronous(lease);
        domain.JoinAsynchronous(lease).Succeeded.ShouldBeTrue();
        lease.Dispose();

        Should.Throw<ObjectDisposedException>(() => domain.IsAsynchronousComplete(lease));
    }

    [Test]
    public void CancelBuildingRental_ReleasesWithoutJoin()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = RentSingle(domain, executor, NoOperation, lane: 1);

        domain.CancelAsynchronous(lease);
        lease.Dispose();

        domain.AsynchronousMetrics.OutstandingBatchCount.ShouldBe(0);
        domain.TryRentAsynchronousBatch(executor, 1, 0, out RenderWorkBatchLease next).IsAdmitted.ShouldBeTrue();
        next.Dispose();
    }

    [Test]
    public void CancelBeforeStart_InvalidatesQueuedWork()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        domain.TryRentAsynchronousBatch(executor, 2, 0, out RenderWorkBatchLease lease).IsAdmitted.ShouldBeTrue();
        lease.SetItem(0, new RenderWorkItem(BlockOperation, 0, 1, PreferredLane: 1));
        // The second item faults if it runs, so a canceled result proves it never started.
        lease.SetItem(1, new RenderWorkItem(FaultOperation, 1, 1, PreferredLane: 1));
        domain.TryReserveAsynchronousLanes(lease, 1UL << 1).IsAdmitted.ShouldBeTrue();

        domain.SubmitAsynchronous(lease, executor, frameSlot: 0);
        executor.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
        domain.CancelAsynchronous(lease);
        executor.Release.Set();

        domain.JoinAsynchronous(lease).IsCanceled.ShouldBeTrue();
        lease.Dispose();
    }

    [Test]
    public void LaneReservation_RejectsOverlapAndIsReleasedByJoinAndRelease()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var first = new SchedulerTestRenderWorkExecutor();
        using var second = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease firstLease = RentSingle(domain, first, NoOperation, lane: 1);
        domain.AsynchronousLaneMask.ShouldBe(1UL << 1);

        domain.TryRentAsynchronousBatch(second, 1, 0, out RenderWorkBatchLease secondLease).IsAdmitted.ShouldBeTrue();
        secondLease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        domain.TryReserveAsynchronousLanes(secondLease, 1UL << 1).Reason
            .ShouldBe(ERenderWorkAdmissionReason.LaneReserved);
        Should.Throw<InvalidOperationException>(() => domain.SubmitAsynchronous(secondLease, second, frameSlot: 0));

        domain.SubmitAsynchronous(firstLease, first, frameSlot: 0);
        domain.JoinAsynchronous(firstLease).Succeeded.ShouldBeTrue();
        domain.AsynchronousLaneMask.ShouldBe(0UL);
        firstLease.Dispose();

        domain.TryReserveAsynchronousLanes(secondLease, 1UL << 1).IsAdmitted.ShouldBeTrue();
        secondLease.Dispose();
        domain.AsynchronousLaneMask.ShouldBe(0UL);
    }

    [Test]
    public void LaneReservation_RejectsInvalidMasks()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        domain.TryRentAsynchronousBatch(executor, 1, 0, out RenderWorkBatchLease lease).IsAdmitted.ShouldBeTrue();

        Should.Throw<ArgumentOutOfRangeException>(() => domain.TryReserveAsynchronousLanes(lease, 0UL));
        Should.Throw<ArgumentOutOfRangeException>(() => domain.TryReserveAsynchronousLanes(lease, 1UL));
        Should.Throw<ArgumentOutOfRangeException>(() => domain.TryReserveAsynchronousLanes(lease, 1UL << 2));
        lease.Dispose();
    }

    [Test]
    public void ReservedLane_RejectsSynchronousAffineWorkAndTakesNoMigratableWork()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var asynchronous = new SchedulerTestRenderWorkExecutor();
        using var synchronous = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease asyncLease = RentSingle(domain, asynchronous, BlockOperation, lane: 1);
        domain.SubmitAsynchronous(asyncLease, asynchronous, frameSlot: 0);
        asynchronous.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();

        RenderWorkBatchLease affine = domain.RentBatch(1);
        affine.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        Should.Throw<InvalidOperationException>(() =>
        {
            RenderWorkBatchLease copy = affine;
            domain.ExecuteAndWait(ref copy, synchronous);
        });
        affine.Dispose();
        domain.AsynchronousMetrics.AffineCollisionCount.ShouldBe(1);

        const int migratableCount = 8;
        RenderWorkBatchLease migratable = domain.RentBatch(migratableCount);
        for (int index = 0; index < migratableCount; index++)
            migratable.SetItem(index, new RenderWorkItem(NoOperation, index, 1, EstimatedCost: 1000));
        domain.ExecuteAndWait(ref migratable, synchronous).Succeeded.ShouldBeTrue();
        migratable.Dispose();
        (synchronous.LaneMask & (1 << 1)).ShouldBe(0);

        asynchronous.Release.Set();
        domain.JoinAsynchronous(asyncLease).Succeeded.ShouldBeTrue();
        asyncLease.Dispose();
    }

    [Test]
    public void Fault_IsQuarantinedOnlyByTheAsynchronousJoin()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var faulting = new SchedulerTestRenderWorkExecutor();
        using var synchronous = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = RentSingle(domain, faulting, FaultOperation, lane: 1);

        domain.SubmitAsynchronous(lease, faulting, frameSlot: 0);
        SpinUntilComplete(domain, lease);

        // A later synchronous batch sweeps quarantines, but not this unjoined generation.
        RenderWorkBatchLease synchronousLease = domain.RentBatch(1);
        synchronousLease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 2));
        domain.ExecuteAndWait(ref synchronousLease, synchronous).Succeeded.ShouldBeTrue();
        synchronousLease.Dispose();
        faulting.QuarantineCount.ShouldBe(0);

        RenderWorkBatchResult result = domain.JoinAsynchronous(lease);
        result.IsFaulted.ShouldBeTrue();
        faulting.QuarantineCount.ShouldBe(1);
        domain.JoinAsynchronous(lease).IsFaulted.ShouldBeTrue();
        faulting.QuarantineCount.ShouldBe(1);
        lease.Dispose();
        domain.AsynchronousMetrics.FaultedCount.ShouldBe(1);
    }

    [Test]
    public void SynchronousWork_CompletesWhileAsynchronousWorkIsBlocked()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var asynchronous = new SchedulerTestRenderWorkExecutor();
        using var synchronous = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease asyncLease = RentSingle(domain, asynchronous, BlockOperation, lane: 1);
        domain.SubmitAsynchronous(asyncLease, asynchronous, frameSlot: 0);
        asynchronous.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();

        RenderWorkBatchLease syncLease = domain.RentBatch(2);
        syncLease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 0));
        syncLease.SetItem(1, new RenderWorkItem(NoOperation, 1, 1, PreferredLane: 2));
        domain.ExecuteAndWait(ref syncLease, synchronous).Succeeded.ShouldBeTrue();
        syncLease.Dispose();

        domain.IsAsynchronousComplete(asyncLease).ShouldBeFalse();
        asynchronous.Release.Set();
        domain.JoinAsynchronous(asyncLease).Succeeded.ShouldBeTrue();
        asynchronous.LaneMask.ShouldBe(1 << 1);
        asyncLease.Dispose();
    }

    [Test]
    public void SynchronousLaneClaim_BlocksReservationUntilTheBatchReturns()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var synchronous = new SchedulerTestRenderWorkExecutor();
        using var asynchronous = new SchedulerTestRenderWorkExecutor();

        RenderWorkBatchLease syncLease = domain.RentBatch(1);
        domain.TryClaimSynchronousLanes(syncLease, (1UL << 0) | (1UL << 1)).IsAdmitted.ShouldBeTrue();
        domain.TryRentAsynchronousBatch(asynchronous, 1, 0, out RenderWorkBatchLease asyncLease).IsAdmitted.ShouldBeTrue();
        domain.TryReserveAsynchronousLanes(asyncLease, 1UL << 1).Reason
            .ShouldBe(ERenderWorkAdmissionReason.LaneClaimedBySynchronousWork);
        domain.TryReserveAsynchronousLanes(asyncLease, 1UL << 2).IsAdmitted.ShouldBeTrue();

        syncLease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        domain.ExecuteAndWait(ref syncLease, synchronous).Succeeded.ShouldBeTrue();
        syncLease.Dispose();

        // The returned batch no longer claims lane 1.
        RenderWorkBatchLease next = domain.RentBatch(1);
        domain.TryClaimSynchronousLanes(next, 1UL << 2).Reason
            .ShouldBe(ERenderWorkAdmissionReason.LaneReserved);
        next.Dispose();
        asyncLease.Dispose();

        domain.TryRentAsynchronousBatch(asynchronous, 1, 0, out RenderWorkBatchLease later).IsAdmitted.ShouldBeTrue();
        domain.TryReserveAsynchronousLanes(later, 1UL << 1).IsAdmitted.ShouldBeTrue();
        later.Dispose();
    }

    [Test]
    public void SynchronousLaneClaim_EndsWhenABuildingLeaseIsAborted()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var asynchronous = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease syncLease = domain.RentBatch(1);
        domain.TryClaimSynchronousLanes(syncLease, 1UL << 1).IsAdmitted.ShouldBeTrue();
        syncLease.Dispose();

        domain.TryRentAsynchronousBatch(asynchronous, 1, 0, out RenderWorkBatchLease asyncLease).IsAdmitted.ShouldBeTrue();
        domain.TryReserveAsynchronousLanes(asyncLease, 1UL << 1).IsAdmitted.ShouldBeTrue();
        asyncLease.Dispose();
    }

    [Test]
    public void ClaimedLease_ShrinksToTheConfiguredItemsAndExecutes()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var synchronous = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = domain.RentBatch(3);
        domain.TryClaimSynchronousLanes(lease, (1UL << 0) | (1UL << 1) | (1UL << 2)).IsAdmitted.ShouldBeTrue();

        lease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        Should.Throw<ArgumentOutOfRangeException>(() => lease.ShrinkItemCount(4));
        Should.Throw<InvalidOperationException>(() => lease.ShrinkItemCount(0));
        lease.ShrinkItemCount(1);
        lease.ItemCount.ShouldBe(1);

        domain.ExecuteAndWait(ref lease, synchronous).Succeeded.ShouldBeTrue();
        lease.Dispose();
    }

    [Test]
    public void WarmOwnerPath_AllocatesNoManagedMemory()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        for (int warm = 0; warm < 4; warm++)
            RunNoOperationCycle(domain, executor);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int cycle = 0; cycle < 16; cycle++)
            RunNoOperationCycle(domain, executor);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        domain.AsynchronousMetrics.SubmitCount.ShouldBe(20);
        domain.AsynchronousMetrics.CompletedCount.ShouldBe(20);
    }

    private static void RunNoOperationCycle(RenderWorkDomain domain, SchedulerTestRenderWorkExecutor executor)
    {
        domain.TryRentAsynchronousBatch(executor, 1, 0, out RenderWorkBatchLease lease).IsAdmitted.ShouldBeTrue();
        lease.SetItem(0, new RenderWorkItem(NoOperation, 0, 1, PreferredLane: 1));
        domain.TryReserveAsynchronousLanes(lease, 1UL << 1).IsAdmitted.ShouldBeTrue();
        domain.SubmitAsynchronous(lease, executor, frameSlot: 0);
        _ = domain.IsAsynchronousComplete(lease);
        if (!domain.JoinAsynchronous(lease).Succeeded)
            throw new InvalidOperationException("The no-operation asynchronous batch did not complete.");
        lease.Dispose();
    }

    private static RenderWorkBatchLease RentSingle(
        RenderWorkDomain domain,
        SchedulerTestRenderWorkExecutor executor,
        int operation,
        int lane)
    {
        domain.TryRentAsynchronousBatch(executor, 1, 0, out RenderWorkBatchLease lease).IsAdmitted.ShouldBeTrue();
        lease.SetItem(0, new RenderWorkItem(operation, 0, 1, PreferredLane: lane));
        if (lane >= 1 && lane < domain.LogicalLaneCount)
            domain.TryReserveAsynchronousLanes(lease, 1UL << lane).IsAdmitted.ShouldBeTrue();
        return lease;
    }

    private static void SpinUntilComplete(RenderWorkDomain domain, in RenderWorkBatchLease lease)
    {
        RenderWorkBatchLease copy = lease;
        SpinWait.SpinUntil(() => domain.IsAsynchronousComplete(copy), TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }
}
