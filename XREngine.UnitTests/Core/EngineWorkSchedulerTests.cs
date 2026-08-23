using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class EngineWorkSchedulerTests
{
    [Test]
    public void ZeroGeneralWorkers_UsesCooperativeInlineExecutionWithoutHiddenThread()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(new EngineExecutionTopologyRequest
        {
            EffectiveProcessorCount = 4,
            GeneralWorkerThreadCount = 0,
            GeneralWorkerThreadCap = 16,
            RenderWorkerThreadCount = 0,
            RenderWorkerThreadCap = 8,
            ReservedForegroundThreadCount = 4,
            DedicatedBackgroundThreadCount = 0,
            AllowCpuOversubscription = false,
            RenderWorkerQos = ERenderWorkerQos.OsDefault,
        });
        var scheduler = new EngineWorkScheduler(topology);
        int executingThreadId = 0;
        int callingThreadId = Environment.CurrentManagedThreadId;

        try
        {
            JobHandle handle = scheduler.GeneralJobs.Schedule(
                new ActionJob(() => executingThreadId = Environment.CurrentManagedThreadId));

            handle.IsCompleted.ShouldBeTrue();
            executingThreadId.ShouldBe(callingThreadId);
            scheduler.Metrics.GeneralWorkerCount.ShouldBe(0);
        }
        finally
        {
            scheduler.Shutdown(waitForWorkers: true).ShouldBeTrue();
        }
    }

    [Test]
    public void NestedZeroWorkerSchedulers_DrainBothDomainsAndRestoreWorkerContext()
    {
        var outer = new EngineWorkScheduler(CreateZeroGeneralTopology());
        var inner = new EngineWorkScheduler(CreateZeroGeneralTopology());
        bool innerRan = false;
        bool outerContextRestored = false;

        try
        {
            JobHandle outerHandle = outer.GeneralJobs.Schedule(new ActionJob(() =>
            {
                JobManager.IsJobWorkerThread.ShouldBeTrue();
                JobManager.CurrentGeneralWorkerLaneId.ShouldBe(0);

                JobHandle innerHandle = inner.GeneralJobs.Schedule(new ActionJob(() =>
                {
                    JobManager.IsJobWorkerThread.ShouldBeTrue();
                    JobManager.CurrentGeneralWorkerLaneId.ShouldBe(0);
                    innerRan = true;
                }));

                innerHandle.IsCompleted.ShouldBeTrue();
                outerContextRestored = JobManager.IsJobWorkerThread &&
                    JobManager.CurrentGeneralWorkerLaneId == 0;
            }));

            outerHandle.IsCompleted.ShouldBeTrue();
            innerRan.ShouldBeTrue();
            outerContextRestored.ShouldBeTrue();
            JobManager.IsJobWorkerThread.ShouldBeFalse();
            JobManager.CurrentGeneralWorkerLaneId.ShouldBe(-1);
        }
        finally
        {
            inner.Shutdown(waitForWorkers: true).ShouldBeTrue();
            outer.Shutdown(waitForWorkers: true).ShouldBeTrue();
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(EngineExecutionTopology.AutomaticWorkerCount)]
    public void RenderWorkerModes_ProduceDeterministicOutputAndCleanShutdown(int requestedWorkers)
    {
        EngineExecutionTopology topology = CreateTopology(requestedWorkers);
        var scheduler = new EngineWorkScheduler(topology, generalQueueLimit: 64);
        int[] output = new int[64];
        using var executor = new SchedulerTestRenderWorkExecutor(output);

        try
        {
            RenderWorkBatchResult result = ExecuteFlatBatch(scheduler.Render, executor, output.Length, operationKind: 1);

            result.Succeeded.ShouldBeTrue();
            for (int index = 0; index < output.Length; index++)
                output[index].ShouldBe(unchecked(((index + 1) * 31) ^ 0x5A5A));
            scheduler.Render.LogicalLaneCount.ShouldBe(topology.RenderWorkerThreadCount + 1);
        }
        finally
        {
            scheduler.Shutdown(waitForWorkers: true).ShouldBeTrue();
        }
    }

    [Test]
    public void TinyBatch_ExecutesOnlyOnParticipatingLaneZero()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor(new int[4]);
        long workerItemsBefore = domain.Metrics.WorkerItemCount;

        RenderWorkBatchResult result = ExecuteFlatBatch(domain, executor, itemCount: 4, operationKind: 1);

        result.Succeeded.ShouldBeTrue();
        executor.LaneMask.ShouldBe(1);
        domain.Metrics.WorkerItemCount.ShouldBe(workerItemsBefore);
        domain.Metrics.InlineItemCount.ShouldBe(4);
    }

    [Test]
    public void LargeBatch_UsesRealWorkerOverlapAndBoundedWait()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();

        RenderWorkBatchResult result = ExecuteFlatBatch(domain, executor, itemCount: 32, operationKind: 2);

        result.Succeeded.ShouldBeTrue();
        executor.PeakConcurrency.ShouldBeGreaterThanOrEqualTo(2);
        domain.Metrics.WorkerItemCount.ShouldBeGreaterThan(0);
        domain.Metrics.InlineItemCount.ShouldBe(0);
    }

    [Test]
    public void DependencyDiamond_PublishesOnlyAfterPrerequisitesComplete()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = domain.RentBatch(itemCount: 4, dependencyCount: 4);
        try
        {
            lease.SetItem(0, new RenderWorkItem(10, 0, 1, 0, 0, 2));
            lease.SetItem(1, new RenderWorkItem(11, 1, 1, 1, 2, 1));
            lease.SetItem(2, new RenderWorkItem(12, 2, 1, 1, 3, 1));
            lease.SetItem(3, new RenderWorkItem(13, 3, 1, 2));
            lease.SetDependent(0, 1);
            lease.SetDependent(1, 2);
            lease.SetDependent(2, 3);
            lease.SetDependent(3, 3);

            RenderWorkBatchResult result = domain.ExecuteAndWait(ref lease, executor);

            result.Succeeded.ShouldBeTrue();
            executor.DependencyState.ShouldBe(0xF);
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Test]
    public void ExecutorFault_InvalidatesWholeBatchAndInvokesQuarantineOnce()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        int ownerThreadId = Environment.CurrentManagedThreadId;

        RenderWorkBatchResult result = ExecuteFlatBatch(domain, executor, itemCount: 8, operationKind: 20);

        result.IsFaulted.ShouldBeTrue();
        result.Exception.ShouldBeOfType<InvalidOperationException>();
        executor.QuarantineCount.ShouldBe(1);
        executor.QuarantineThreadId.ShouldBe(ownerThreadId);
        domain.Metrics.ActiveBatchCount.ShouldBe(0);
    }

    [Test]
    public void Cancellation_StopsNewClaimsAndWaitsForActiveClaimToExit()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = domain.RentBatch(1);
        lease.SetItem(0, new RenderWorkItem(40, 0, 1, PreferredLane: 1));

        Task<RenderWorkBatchResult> execution = Task.Run(() =>
        {
            RenderWorkBatchLease executingLease = lease;
            return domain.ExecuteAndWait(ref executingLease, executor);
        });

        try
        {
            executor.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
            lease.Cancel();
            executor.Release.Set();

            RenderWorkBatchResult result = execution.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            result.IsCanceled.ShouldBeTrue();
        }
        finally
        {
            executor.Release.Set();
            lease.Dispose();
        }
    }

    [Test]
    public void ActiveExecutorFault_UpgradesConcurrentCancellationAndQuarantinesOnce()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = domain.RentBatch(1);
        lease.SetItem(0, new RenderWorkItem(41, 0, 1, PreferredLane: 1));

        Task<RenderWorkBatchResult> execution = Task.Run(() =>
        {
            RenderWorkBatchLease executingLease = lease;
            return domain.ExecuteAndWait(ref executingLease, executor);
        });

        try
        {
            executor.Entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();
            lease.Cancel();
            executor.Release.Set();

            RenderWorkBatchResult result = execution.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            result.IsFaulted.ShouldBeTrue();
            result.Exception.ShouldBeOfType<InvalidOperationException>();
            executor.QuarantineCount.ShouldBe(1);
            domain.Metrics.ActiveBatchCount.ShouldBe(0);
            domain.Metrics.CanceledBatchCount.ShouldBe(0);
            domain.Metrics.CanceledItemCount.ShouldBe(0);
            domain.Metrics.FaultedBatchCount.ShouldBe(1);
        }
        finally
        {
            executor.Release.Set();
            lease.Dispose();
        }
    }

    [Test]
    public void BoundedQueueOverflow_FaultsVisiblyInsteadOfDroppingWork()
    {
        using var domain = new RenderWorkDomain(
            backgroundWorkerCount: 0,
            ERenderWorkerQos.OsDefault,
            queueCapacityPerLane: 1);
        using var executor = new SchedulerTestRenderWorkExecutor();

        RenderWorkBatchResult result = ExecuteFlatBatch(domain, executor, itemCount: 3, operationKind: 50);

        result.IsFaulted.ShouldBeTrue();
        result.Exception!.Message.ShouldContain("Bounded render queue");
        domain.Metrics.QueueOverflowCount.ShouldBe(1);
    }

    [Test]
    public void LaneLocalAttachment_IsVisibleOnlyToItsAffineLaneAndCanBeCleared()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);
        object marker = new();
        domain.BackendAttachments.Register(laneId: 1, frameSlot: 0, marker).ShouldBeNull();
        using var executor = new SchedulerTestRenderWorkExecutor(expectedAttachment: marker);
        RenderWorkBatchLease lease = domain.RentBatch(1);
        try
        {
            lease.SetItem(0, new RenderWorkItem(30, 0, 1, PreferredLane: 1));
            RenderWorkBatchResult result = domain.ExecuteAndWait(ref lease, executor);
            result.Succeeded.ShouldBeTrue();
        }
        finally
        {
            lease.Dispose();
        }

        domain.BackendAttachments.Register(laneId: 1, frameSlot: 0, attachment: null).ShouldBeSameAs(marker);
        domain.BackendAttachments.Get(laneId: 1, frameSlot: 0).ShouldBeNull();
    }

    [Test]
    public void StaleLease_CannotMutateReusedPooledGeneration()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease stale = domain.RentBatch(1);
        stale.SetItem(0, new RenderWorkItem(50, 0, 1));
        RenderWorkBatchLease executing = stale;
        domain.ExecuteAndWait(ref executing, executor).Succeeded.ShouldBeTrue();
        stale.Dispose();

        RenderWorkBatchLease current = domain.RentBatch(1);
        try
        {
            Should.Throw<ObjectDisposedException>(() => stale.SetItem(0, new RenderWorkItem(50, 0, 1)));
            current.SetItem(0, new RenderWorkItem(50, 0, 1));
            domain.ExecuteAndWait(ref current, executor).Succeeded.ShouldBeTrue();
        }
        finally
        {
            current.Dispose();
        }
    }

    [Test]
    public void WarmInlineBatches_AllocateZeroManagedBytesOnCallingThread()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();

        ExecuteFlatBatch(domain, executor, itemCount: 1, operationKind: 50).Succeeded.ShouldBeTrue();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            if (!ExecuteFlatBatch(domain, executor, itemCount: 1, operationKind: 50).Succeeded)
                throw new InvalidOperationException("Warm allocation probe batch failed.");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
    }

    [Test]
    public void CompletedDiagnosticPayload_CannotRepresentPendingGpuSynchronization()
    {
        Type[] fieldTypes = typeof(CompletedDiagnosticPayload)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        fieldTypes.ShouldContain(typeof(ArraySegment<uint>));
        fieldTypes.ShouldNotContain(typeof(ReadOnlyMemory<uint>));
        fieldTypes.ShouldNotContain(typeof(Task));
        fieldTypes.ShouldNotContain(typeof(WaitHandle));
        fieldTypes.Any(type => typeof(Task).IsAssignableFrom(type)).ShouldBeFalse();
        fieldTypes.Any(type => typeof(WaitHandle).IsAssignableFrom(type)).ShouldBeFalse();
    }

    [Test]
    public void LeaseFromAnotherDomain_IsRejectedBeforeEitherDomainQueuesWork()
    {
        using var owner = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        using var other = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        RenderWorkBatchLease lease = owner.RentBatch(1);
        try
        {
            lease.SetItem(0, new RenderWorkItem(50, 0, 1));

            Should.Throw<ArgumentException>(() => other.ExecuteAndWait(ref lease, executor));
            owner.Metrics.SubmittedBatchCount.ShouldBe(0);
            other.Metrics.SubmittedBatchCount.ShouldBe(0);
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Test]
    public void RenderShutdown_FastSignalCanBeJoinedAndRepeated()
    {
        var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);

        domain.Shutdown(waitForWorkers: false).ShouldBeFalse();
        domain.Shutdown(waitForWorkers: true).ShouldBeTrue();
        domain.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void RenderShutdown_HeldLeasePreventsDisposalUntilLeaseReturns()
    {
        var domain = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        RenderWorkBatchLease lease = domain.RentBatch(1);
        lease.SetItem(0, new RenderWorkItem(50, 0, 1));

        domain.Shutdown(waitForWorkers: false).ShouldBeFalse();
        domain.Shutdown(waitForWorkers: true, TimeSpan.FromMilliseconds(25)).ShouldBeFalse();

        lease.Dispose();
        domain.Shutdown(waitForWorkers: true).ShouldBeTrue();
        domain.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    [Test]
    public void IdleRenderWorkers_DoNotManufactureWakeupTelemetry()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 1, ERenderWorkerQos.OsDefault);

        Thread.Sleep(120);

        domain.Metrics.WakeCount.ShouldBe(0);
        domain.Metrics.EmptyWakeCount.ShouldBe(0);
    }

    [Test]
    public void IdleLaneZero_CanRebindForTheCandidateBatchOnAnotherThread()
    {
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 0, ERenderWorkerQos.OsDefault);
        using var executor = new SchedulerTestRenderWorkExecutor();
        int firstOwnerThreadId = Environment.CurrentManagedThreadId;

        ExecuteFlatBatch(domain, executor, itemCount: 1, operationKind: 50).Succeeded.ShouldBeTrue();
        domain.GetLaneSnapshot(0).ManagedThreadId.ShouldBe(firstOwnerThreadId);

        int secondOwnerThreadId = Task.Run(() =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            ExecuteFlatBatch(domain, executor, itemCount: 1, operationKind: 50).Succeeded.ShouldBeTrue();
            return threadId;
        }).GetAwaiter().GetResult();

        secondOwnerThreadId.ShouldNotBe(firstOwnerThreadId);
        domain.GetLaneSnapshot(0).ManagedThreadId.ShouldBe(secondOwnerThreadId);
    }

    [Test]
    public void SchedulerShutdown_ReportsBlockedGeneralWorkerAndSucceedsAfterRelease()
    {
        EngineExecutionTopology topology = CreateTopology(requestedRenderWorkers: 0);
        var scheduler = new EngineWorkScheduler(topology, generalQueueLimit: 64);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        scheduler.GeneralJobs.Schedule(new ActionJob(() =>
        {
            entered.Set();
            release.Wait();
        }));
        entered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();

        try
        {
            scheduler.Shutdown(waitForWorkers: false).ShouldBeFalse();
            scheduler.Shutdown(waitForWorkers: true, TimeSpan.FromMilliseconds(25)).ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }

        scheduler.Shutdown(waitForWorkers: true).ShouldBeTrue();
        scheduler.Shutdown(waitForWorkers: true).ShouldBeTrue();
    }

    private static RenderWorkBatchResult ExecuteFlatBatch(
        RenderWorkDomain domain,
        SchedulerTestRenderWorkExecutor executor,
        int itemCount,
        int operationKind)
    {
        RenderWorkBatchLease lease = domain.RentBatch(itemCount);
        try
        {
            for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
                lease.SetItem(itemIndex, new RenderWorkItem(operationKind, itemIndex, 1));

            return domain.ExecuteAndWait(ref lease, executor);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static EngineExecutionTopology CreateTopology(int requestedRenderWorkers)
        => EngineExecutionTopology.Resolve(new EngineExecutionTopologyRequest
        {
            EffectiveProcessorCount = 64,
            GeneralWorkerThreadCount = 1,
            GeneralWorkerThreadCap = 16,
            RenderWorkerThreadCount = requestedRenderWorkers,
            RenderWorkerThreadCap = 8,
            ReservedForegroundThreadCount = 4,
            DedicatedBackgroundThreadCount = 0,
            AllowCpuOversubscription = false,
            RenderWorkerQos = ERenderWorkerQos.OsDefault,
        });

    private static EngineExecutionTopology CreateZeroGeneralTopology()
        => EngineExecutionTopology.Resolve(new EngineExecutionTopologyRequest
        {
            EffectiveProcessorCount = 4,
            GeneralWorkerThreadCount = 0,
            GeneralWorkerThreadCap = 16,
            RenderWorkerThreadCount = 0,
            RenderWorkerThreadCap = 8,
            ReservedForegroundThreadCount = 4,
            DedicatedBackgroundThreadCount = 0,
            AllowCpuOversubscription = false,
            RenderWorkerQos = ERenderWorkerQos.OsDefault,
        });
}
