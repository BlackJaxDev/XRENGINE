using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainReadbackTransferTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static readonly PhysicsChainReadbackSourceEpoch Epoch = new(1u, 1u, 1u);

    [Test]
    public void PackedValueTypes_MatchPublishedLayoutSizes()
    {
        Marshal.SizeOf<PhysicsChainParticleReadbackValue>().ShouldBe(PhysicsChainReadbackLayout.ParticleByteCount);
        Marshal.SizeOf<PhysicsChainAffineTransformReadbackValue>().ShouldBe(PhysicsChainReadbackLayout.AffineTransformByteCount);
        Marshal.SizeOf<PhysicsChainBoundsReadbackValue>().ShouldBe(PhysicsChainReadbackLayout.BoundsByteCount);
        Marshal.SizeOf<PhysicsChainCollisionEventReadbackValue>().ShouldBe(PhysicsChainReadbackLayout.CollisionEventByteCount);
    }

    [Test]
    public void PendingPlanDrain_IsBoundedAndPreservesRequestOrder()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            PhysicsChainReadbackHandle[] handles = new PhysicsChainReadbackHandle[3];
            for (int i = 0; i < handles.Length; ++i)
                Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [i], 24, 9L, out handles[i]);

            PhysicsChainReadbackGatherPlan?[] firstBatch = new PhysicsChainReadbackGatherPlan?[2];
            scheduler.BuildPendingReadbackGatherPlans(Epoch, 9L, firstBatch).ShouldBe(2);
            firstBatch[0]!.RequestHandle.ShouldBe(handles[0]);
            firstBatch[1]!.RequestHandle.ShouldBe(handles[1]);

            PhysicsChainReadbackGatherPlan?[] secondBatch = new PhysicsChainReadbackGatherPlan?[2];
            scheduler.BuildPendingReadbackGatherPlans(Epoch, 9L, secondBatch).ShouldBe(1);
            secondBatch[0]!.RequestHandle.ShouldBe(handles[2]);
            secondBatch[1].ShouldBeNull();
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void GatherPlan_IsTypedTightlyPackedAndPreservesSelectionOrder()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            PhysicsChainReadbackFields fields = PhysicsChainReadbackFields.Particles
                | PhysicsChainReadbackFields.Bones
                | PhysicsChainReadbackFields.Bounds;
            PhysicsChainReadbackLayout.TryCalculate(fields, 2, out int elements, out int bytes).ShouldBeTrue();
            elements.ShouldBe(6);
            bytes.ShouldBe(192);
            Request(scheduler, component, fields, [5, 2], bytes, 10L, out PhysicsChainReadbackHandle handle);

            scheduler.TryBuildReadbackGatherPlan(handle, Epoch, 10L, out PhysicsChainReadbackGatherPlan? plan, out _).ShouldBeTrue();
            plan.ShouldNotBeNull();
            PhysicsChainReadbackGatherItem[] items = plan!.Items.ToArray();
            items.ShouldBe(
            [
                new(PhysicsChainReadbackElementKind.Particle, 5, 0, 24),
                new(PhysicsChainReadbackElementKind.Particle, 2, 24, 24),
                new(PhysicsChainReadbackElementKind.Bone, 5, 48, 48),
                new(PhysicsChainReadbackElementKind.Bone, 2, 96, 48),
                new(PhysicsChainReadbackElementKind.Bounds, 5, 144, 24),
                new(PhysicsChainReadbackElementKind.Bounds, 2, 168, 24),
            ]);
            plan.ElementCount.ShouldBe(6);
            plan.ByteCount.ShouldBe(192);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void RequestRejectsByteMismatchAndLayoutOverflow()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            scheduler.TryRequestReadback(
                component.RuntimeHandle,
                PhysicsChainReadbackFields.Bounds,
                [0],
                23,
                1L,
                out _,
                out PhysicsChainReadbackRejection rejection).ShouldBeFalse();
            rejection.ShouldBe(PhysicsChainReadbackRejection.ByteCountMismatch);

            PhysicsChainReadbackLayout.TryCalculate(
                PhysicsChainReadbackFields.Particles | PhysicsChainReadbackFields.Bones,
                int.MaxValue,
                out int elements,
                out int bytes).ShouldBeFalse();
            elements.ShouldBe(0);
            bytes.ShouldBe(0);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void ElementBudget_ChargesEveryTypedOutputInMultiFieldRequest()
    {
        var service = new PhysicsChainReadbackService(new PhysicsChainReadbackLimits(
            MaximumElementsPerFrame: 3,
            MaximumBytesPerFrame: 1024,
            MaximumLifetimeFrames: 8));
        PhysicsChainReadbackFields fields = PhysicsChainReadbackFields.Bones | PhysicsChainReadbackFields.Sockets;
        PhysicsChainReadbackLayout.TryCalculate(fields, 2, out int elements, out int bytes).ShouldBeTrue();
        elements.ShouldBe(4);

        service.TryRequest(
            new PhysicsChainRuntimeHandle(0, 1u),
            fields,
            [0, 1],
            bytes,
            1L,
            out _,
            out PhysicsChainReadbackRejection rejection).ShouldBeFalse();

        rejection.ShouldBe(PhysicsChainReadbackRejection.ElementBudgetExceeded);
        service.GetTransferCounters().RequestedElements.ShouldBe(0L);
        service.GetCounters().Rejected.ShouldBe(1L);
    }

    [Test]
    public void TripleBuffer_RejectsFourthLeaseAndStaleLeaseAfterReuse()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            PhysicsChainReadbackGatherPlan[] plans = new PhysicsChainReadbackGatherPlan[3];
            PhysicsChainReadbackStagingLease[] leases = new PhysicsChainReadbackStagingLease[3];
            for (int i = 0; i < plans.Length; ++i)
            {
                Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [i], 24, 2L, out PhysicsChainReadbackHandle handle);
                scheduler.TryBuildReadbackGatherPlan(handle, Epoch, 2L, out PhysicsChainReadbackGatherPlan? plan, out _).ShouldBeTrue();
                plans[i] = plan.ShouldNotBeNull();
                scheduler.TryAcquireReadbackStagingSlot(plans[i], out leases[i], out _).ShouldBeTrue();
            }

            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [3], 24, 2L, out PhysicsChainReadbackHandle fourthHandle);
            scheduler.TryBuildReadbackGatherPlan(
                fourthHandle,
                Epoch,
                2L,
                out _,
                out PhysicsChainReadbackTransferFailure fullFailure).ShouldBeFalse();
            fullFailure.ShouldBe(PhysicsChainReadbackTransferFailure.NoStagingSlot);

            scheduler.AbandonReadbackStagingSlot(leases[1]).ShouldBeTrue();
            scheduler.TryBuildReadbackGatherPlan(fourthHandle, Epoch, 2L, out PhysicsChainReadbackGatherPlan? fourthPlan, out _).ShouldBeTrue();
            scheduler.TryAcquireReadbackStagingSlot(fourthPlan!, out PhysicsChainReadbackStagingLease reused, out _).ShouldBeTrue();
            reused.Slot.ShouldBe(leases[1].Slot);
            reused.Generation.ShouldNotBe(leases[1].Generation);
            scheduler.CommitReadbackStagingSlot(
                leases[1],
                new byte[24],
                new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
                2L,
                out PhysicsChainReadbackTransferFailure staleLeaseFailure).ShouldBeFalse();
            staleLeaseFailure.ShouldBe(PhysicsChainReadbackTransferFailure.InvalidLease);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void FencePolling_IsNonBlockingAndDeliversOnlyAfterEarliestFrame()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 10L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 10L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            byte[] payload = Enumerable.Range(0, 24).Select(static value => (byte)value).ToArray();
            var fence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            scheduler.CommitReadbackStagingSlot(lease, payload, fence, 10L, out _).ShouldBeTrue();

            scheduler.PollReadbackTransfers(10L, Epoch);
            fence.PollCount.ShouldBe(1);
            scheduler.TryGetReadbackResult(handle, out _).ShouldBeFalse();
            PhysicsChainReadbackTransferCounters pending = scheduler.GetReadbackTransferCounters();
            pending.GatheredElements.ShouldBe(1L);
            pending.TransferredElements.ShouldBe(0L);

            fence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(10L, Epoch);
            scheduler.TryGetReadbackResult(handle, out _).ShouldBeFalse();
            scheduler.GetReadbackTransferCounters().TransferredBytes.ShouldBe(24L);

            scheduler.PollReadbackTransfers(11L, Epoch);
            scheduler.TryGetReadbackResult(handle, out PhysicsChainReadbackResult? result).ShouldBeTrue();
            result.ShouldNotBeNull();
            result!.PackedData.Span.SequenceEqual(payload).ShouldBeTrue();
            result.LatencyFrames.ShouldBe(1L);
            PhysicsChainReadbackTransferCounters delivered = scheduler.GetReadbackTransferCounters();
            delivered.RequestedElements.ShouldBe(1L);
            delivered.RequestedBytes.ShouldBe(24L);
            delivered.GatheredElements.ShouldBe(1L);
            delivered.GatheredBytes.ShouldBe(24L);
            delivered.TransferredElements.ShouldBe(1L);
            delivered.TransferredBytes.ShouldBe(24L);
            delivered.DeliveredElements.ShouldBe(1L);
            delivered.DeliveredBytes.ShouldBe(24L);
            delivered.Latency.OneFrame.ShouldBe(1L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void BackendStagingSource_IsCopiedOnlyAfterFenceAndDisposedExactlyOnce()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 12L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 12L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            var source = new TestStagingSource(24);
            var fence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            scheduler.CommitReadbackStagingSlot(lease, source, fence, 12L, out _).ShouldBeTrue();

            scheduler.PollReadbackTransfers(13L, Epoch);
            source.CopyCount.ShouldBe(0);
            source.DisposeCount.ShouldBe(0);

            fence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(14L, Epoch);
            source.CopyCount.ShouldBe(1);
            source.DisposeCount.ShouldBe(1);
            fence.DisposeCount.ShouldBe(1);
            scheduler.TryGetReadbackResult(handle, out PhysicsChainReadbackResult? result).ShouldBeTrue();
            result!.PackedData.Span.ToArray().ShouldAllBe(static value => value == 0x5A);

            scheduler.PollReadbackTransfers(15L, Epoch);
            source.CopyCount.ShouldBe(1);
            source.DisposeCount.ShouldBe(1);
            fence.DisposeCount.ShouldBe(1);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void FailedGather_ReleasesFillingSlotAndMarksRequestFailed()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 14L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 14L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();

            scheduler.FailReadbackStagingSlot(lease, 14L).ShouldBeTrue();
            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
            info!.Status.ShouldBe(PhysicsChainReadbackStatus.Failed);

            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 15L, out PhysicsChainReadbackHandle nextHandle);
            PhysicsChainReadbackGatherPlan nextPlan = BuildPlan(scheduler, nextHandle, Epoch, 15L);
            scheduler.TryAcquireReadbackStagingSlot(nextPlan, out _, out _).ShouldBeTrue();
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void CancellationBeforeCommit_ReleasesFillingSlotAndInvalidatesLease()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 15L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 15L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            scheduler.CancelReadback(handle).ShouldBeTrue();

            scheduler.CommitReadbackStagingSlot(
                lease,
                new byte[24],
                new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
                15L,
                out PhysicsChainReadbackTransferFailure failure).ShouldBeFalse();
            failure.ShouldBe(PhysicsChainReadbackTransferFailure.InvalidLease);

            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [1], 24, 16L, out PhysicsChainReadbackHandle next);
            PhysicsChainReadbackGatherPlan nextPlan = BuildPlan(scheduler, next, Epoch, 16L);
            scheduler.TryAcquireReadbackStagingSlot(nextPlan, out _, out _).ShouldBeTrue();
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void CancellationAfterCommit_DiscardsSignaledTransferWithoutDelivery()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 3L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 3L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            var fence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            scheduler.CommitReadbackStagingSlot(lease, new byte[24], fence, 3L, out _).ShouldBeTrue();
            scheduler.CancelReadback(handle).ShouldBeTrue();

            scheduler.PollReadbackTransfers(4L, Epoch);
            scheduler.GetReadbackTransferCounters().TransferredBytes.ShouldBe(0L);
            fence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(5L, Epoch);

            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
            info!.Status.ShouldBe(PhysicsChainReadbackStatus.Cancelled);
            scheduler.TryGetReadbackResult(handle, out _).ShouldBeFalse();
            PhysicsChainReadbackTransferCounters counters = scheduler.GetReadbackTransferCounters();
            counters.TransferredBytes.ShouldBe(24L);
            counters.DiscardedStaleBytes.ShouldBe(24L);
            counters.DeliveredBytes.ShouldBe(0L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void ExpiredRequest_RemainsQuarantinedUntilFenceSignalsThenDiscards()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 20L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 20L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            var fence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            scheduler.CommitReadbackStagingSlot(lease, new byte[24], fence, 20L, out _).ShouldBeTrue();
            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();

            scheduler.PollReadbackTransfers(info!.ExpiryFrame, Epoch);
            info.Status.ShouldBe(PhysicsChainReadbackStatus.Expired);
            scheduler.GetReadbackTransferCounters().DiscardedStaleBytes.ShouldBe(0L);

            fence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(info.ExpiryFrame + 1L, Epoch);
            info.Status.ShouldBe(PhysicsChainReadbackStatus.Expired);
            scheduler.GetReadbackTransferCounters().DiscardedStaleBytes.ShouldBe(24L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void OutOfOrderFenceSignals_DeliverOnlyTheirMatchingRequestPayload()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 30L, out PhysicsChainReadbackHandle first);
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [1], 24, 30L, out PhysicsChainReadbackHandle second);
            PhysicsChainReadbackGatherPlan firstPlan = BuildPlan(scheduler, first, Epoch, 30L);
            PhysicsChainReadbackGatherPlan secondPlan = BuildPlan(scheduler, second, Epoch, 30L);
            scheduler.TryAcquireReadbackStagingSlot(firstPlan, out PhysicsChainReadbackStagingLease firstLease, out _).ShouldBeTrue();
            scheduler.TryAcquireReadbackStagingSlot(secondPlan, out PhysicsChainReadbackStagingLease secondLease, out _).ShouldBeTrue();
            var firstFence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            var secondFence = new TestFence(PhysicsChainReadbackFenceStatus.Signaled);
            byte[] firstPayload = Enumerable.Repeat((byte)0x11, 24).ToArray();
            byte[] secondPayload = Enumerable.Repeat((byte)0x22, 24).ToArray();
            scheduler.CommitReadbackStagingSlot(firstLease, firstPayload, firstFence, 30L, out _).ShouldBeTrue();
            scheduler.CommitReadbackStagingSlot(secondLease, secondPayload, secondFence, 30L, out _).ShouldBeTrue();

            scheduler.PollReadbackTransfers(31L, Epoch);
            scheduler.TryGetReadbackResult(first, out _).ShouldBeFalse();
            scheduler.TryGetReadbackResult(second, out PhysicsChainReadbackResult? secondResult).ShouldBeTrue();
            secondResult!.PackedData.Span.SequenceEqual(secondPayload).ShouldBeTrue();

            firstFence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(32L, Epoch);
            scheduler.TryGetReadbackResult(first, out PhysicsChainReadbackResult? firstResult).ShouldBeTrue();
            firstResult!.PackedData.Span.SequenceEqual(firstPayload).ShouldBeTrue();
            secondResult.PackedData.Span.SequenceEqual(secondPayload).ShouldBeTrue();
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [TestCase(2u, 1u, 1u)]
    [TestCase(1u, 2u, 1u)]
    [TestCase(1u, 1u, 2u)]
    public void BackendArenaOrLayoutEpochChange_DiscardsStaleTransfer(
        uint backendGeneration,
        uint arenaGeneration,
        uint layoutGeneration)
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 4L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 4L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            scheduler.CommitReadbackStagingSlot(
                lease,
                new byte[24],
                new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
                4L,
                out _).ShouldBeTrue();

            scheduler.PollReadbackTransfers(
                5L,
                new PhysicsChainReadbackSourceEpoch(backendGeneration, arenaGeneration, layoutGeneration));

            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
            info!.Status.ShouldBe(PhysicsChainReadbackStatus.DiscardedStale);
            scheduler.TryGetReadbackResult(handle, out _).ShouldBeFalse();
            scheduler.GetReadbackTransferCounters().DiscardedStaleBytes.ShouldBe(24L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void DestroyAndRuntimeSlotReuse_CannotDeliverToNewGeneration()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 6L, out PhysicsChainReadbackHandle handle);
        PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 6L);
        scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
        scheduler.CommitReadbackStagingSlot(
            lease,
            new byte[24],
            new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
            6L,
            out _).ShouldBeTrue();
        PhysicsChainRuntimeHandle staleInstance = component.RuntimeHandle;

        Destroy(world, node);
        var replacementNode = new SceneNode("Replacement");
        PhysicsChainComponent replacement = replacementNode.AddComponent<PhysicsChainComponent>()!;
        WorldProperty.SetValue(replacementNode, world);
        world.Invoke(ETickGroup.Normal);
        try
        {
            replacement.RuntimeHandle.Slot.ShouldBe(staleInstance.Slot);
            replacement.RuntimeHandle.Generation.ShouldNotBe(staleInstance.Generation);
            scheduler.PollReadbackTransfers(7L, Epoch);

            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
            info!.Status.ShouldBe(PhysicsChainReadbackStatus.DiscardedStale);
            scheduler.TryGetReadbackResult(handle, out _).ShouldBeFalse();
        }
        finally
        {
            Destroy(world, replacementNode);
        }
    }

    [Test]
    public void ReleasedRequestSlotReuse_CannotReceiveOldTransfer()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 7L, out PhysicsChainReadbackHandle stale);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, stale, Epoch, 7L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            var fence = new TestFence(PhysicsChainReadbackFenceStatus.Pending);
            scheduler.CommitReadbackStagingSlot(lease, new byte[24], fence, 7L, out _).ShouldBeTrue();
            scheduler.CancelReadback(stale).ShouldBeTrue();
            scheduler.ReleaseReadback(stale).ShouldBeTrue();

            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [1], 24, 8L, out PhysicsChainReadbackHandle current);
            current.Slot.ShouldBe(stale.Slot);
            current.Generation.ShouldNotBe(stale.Generation);
            fence.Status = PhysicsChainReadbackFenceStatus.Signaled;
            scheduler.PollReadbackTransfers(8L, Epoch);

            scheduler.TryGetReadbackRequest(current, out PhysicsChainReadbackRequestInfo? currentInfo).ShouldBeTrue();
            currentInfo!.Status.ShouldBe(PhysicsChainReadbackStatus.Pending);
            scheduler.TryGetReadbackResult(current, out _).ShouldBeFalse();
            scheduler.GetReadbackTransferCounters().DiscardedStaleBytes.ShouldBe(24L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void DeliveredLatencyHistogram_AccountsEveryBucketExactly()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            long[] latencies = [1L, 2L, 3L, 4L, 6L];
            for (int i = 0; i < latencies.Length; ++i)
            {
                long submissionFrame = i * 20L + 1L;
                Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [i], 24, submissionFrame, out PhysicsChainReadbackHandle handle);
                PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, submissionFrame);
                scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
                scheduler.CommitReadbackStagingSlot(
                    lease,
                    new byte[24],
                    new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
                    submissionFrame,
                    out _).ShouldBeTrue();
                scheduler.PollReadbackTransfers(submissionFrame + latencies[i], Epoch);
                scheduler.TryGetReadbackResult(handle, out _).ShouldBeTrue();
            }

            PhysicsChainReadbackLatencyHistogram histogram = scheduler.GetReadbackTransferCounters().Latency;
            histogram.OneFrame.ShouldBe(1L);
            histogram.TwoFrames.ShouldBe(1L);
            histogram.ThreeFrames.ShouldBe(1L);
            histogram.FourFrames.ShouldBe(1L);
            histogram.FiveToEightFrames.ShouldBe(1L);
            histogram.NineOrMoreFrames.ShouldBe(0L);

            var longLifetimeService = new PhysicsChainReadbackService(new PhysicsChainReadbackLimits(
                MaximumElementsPerFrame: 4,
                MaximumBytesPerFrame: 1024,
                MaximumLifetimeFrames: 16));
            long longSubmissionFrame = 200L;
            longLifetimeService.TryRequest(
                component.RuntimeHandle,
                PhysicsChainReadbackFields.Bounds,
                [0],
                24,
                longSubmissionFrame,
                out PhysicsChainReadbackHandle longHandle,
                out _).ShouldBeTrue();
            longLifetimeService.TryBuildGatherPlan(longHandle, Epoch, longSubmissionFrame, out PhysicsChainReadbackGatherPlan? longPlan, out _).ShouldBeTrue();
            longLifetimeService.TryAcquireStagingSlot(longPlan!, out PhysicsChainReadbackStagingLease longLease, out _).ShouldBeTrue();
            longLifetimeService.CommitStagingSlot(
                longLease,
                new byte[24],
                new TestFence(PhysicsChainReadbackFenceStatus.Signaled),
                longSubmissionFrame,
                out _).ShouldBeTrue();
            longLifetimeService.AdvanceFrame(longSubmissionFrame + 9L);
            longLifetimeService.PollTransfers(scheduler, longSubmissionFrame + 9L, Epoch);
            longLifetimeService.TryGetResult(longHandle, out _).ShouldBeTrue();
            longLifetimeService.GetTransferCounters().Latency.NineOrMoreFrames.ShouldBe(1L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    [Test]
    public void FailedFence_MakesFailureTerminalWithoutTransferredBytes()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        try
        {
            Request(scheduler, component, PhysicsChainReadbackFields.Bounds, [0], 24, 1L, out PhysicsChainReadbackHandle handle);
            PhysicsChainReadbackGatherPlan plan = BuildPlan(scheduler, handle, Epoch, 1L);
            scheduler.TryAcquireReadbackStagingSlot(plan, out PhysicsChainReadbackStagingLease lease, out _).ShouldBeTrue();
            scheduler.CommitReadbackStagingSlot(
                lease,
                new byte[24],
                new TestFence(PhysicsChainReadbackFenceStatus.Failed),
                1L,
                out _).ShouldBeTrue();
            scheduler.PollReadbackTransfers(2L, Epoch);

            scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
            info!.Status.ShouldBe(PhysicsChainReadbackStatus.Failed);
            scheduler.GetReadbackTransferCounters().TransferredBytes.ShouldBe(0L);
        }
        finally
        {
            Destroy(world, node);
        }
    }

    private static void Request(
        PhysicsChainWorld scheduler,
        PhysicsChainComponent component,
        PhysicsChainReadbackFields fields,
        ReadOnlySpan<int> selection,
        int bytes,
        long frame,
        out PhysicsChainReadbackHandle handle)
    {
        scheduler.TryRequestReadback(
            component.RuntimeHandle,
            fields,
            selection,
            bytes,
            frame,
            out handle,
            out PhysicsChainReadbackRejection rejection).ShouldBeTrue(rejection.ToString());
    }

    private static PhysicsChainReadbackGatherPlan BuildPlan(
        PhysicsChainWorld scheduler,
        PhysicsChainReadbackHandle handle,
        PhysicsChainReadbackSourceEpoch epoch,
        long frame)
    {
        scheduler.TryBuildReadbackGatherPlan(handle, epoch, frame, out PhysicsChainReadbackGatherPlan? plan, out PhysicsChainReadbackTransferFailure failure)
            .ShouldBeTrue(failure.ToString());
        return plan.ShouldNotBeNull();
    }

    private static (TestWorldContext World, PhysicsChainWorld Scheduler, SceneNode Node, PhysicsChainComponent Component) CreateRegisteredComponent()
    {
        var world = new TestWorldContext();
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        WorldProperty.SetValue(node, world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        return (world, scheduler!, node, component);
    }

    private static void Destroy(TestWorldContext world, SceneNode node)
    {
        WorldProperty.SetValue(node, null);
        world.Invoke(ETickGroup.Normal);
    }

    private sealed class TestFence(PhysicsChainReadbackFenceStatus status) : IPhysicsChainReadbackFence, IDisposable
    {
        public PhysicsChainReadbackFenceStatus Status { get; set; } = status;
        public int PollCount { get; private set; }
        public int DisposeCount { get; private set; }

        public PhysicsChainReadbackFenceStatus Poll()
        {
            ++PollCount;
            return Status;
        }

        public void Dispose()
            => ++DisposeCount;
    }

    private sealed class TestStagingSource(int byteCount) : IPhysicsChainReadbackStagingSource
    {
        public int ByteCount { get; } = byteCount;
        public int CopyCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool TryCopyTo(Span<byte> destination)
        {
            ++CopyCount;
            if (destination.Length != ByteCount)
                return false;

            destination.Fill(0x5A);
            return true;
        }

        public void Dispose()
            => ++DisposeCount;
    }

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];
        public bool IsPlaySessionActive => false;
        public void RegisterTick(ETickGroup group, int order, WorldTick tick) => _ticks.Add((group, tick));
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) => _ticks.Remove((group, tick));
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
        public void Invoke(ETickGroup group)
        {
            for (int i = 0; i < _ticks.Count; ++i)
                if (_ticks[i].Group == group)
                    _ticks[i].Tick();
        }
    }
}
