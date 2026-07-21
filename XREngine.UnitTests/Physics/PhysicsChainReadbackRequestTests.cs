using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainReadbackRequestTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void RequestCarriesGenerationFreshnessSelectionAndCancellation()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        int[] selected = [1, 3, 5];

        scheduler.TryRequestReadback(
            component.RuntimeHandle,
            PhysicsChainReadbackFields.Bones | PhysicsChainReadbackFields.Sockets,
            selected,
            expectedByteCount: 3 * 48 * 2,
            submissionFrame: 20L,
            out PhysicsChainReadbackHandle handle,
            out PhysicsChainReadbackRejection rejection).ShouldBeTrue();

        rejection.ShouldBe(PhysicsChainReadbackRejection.None);
        scheduler.TryGetReadbackRequest(handle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
        info.ShouldNotBeNull();
        info!.InstanceHandle.ShouldBe(component.RuntimeHandle);
        info.SubmissionFrame.ShouldBe(20L);
        info.EarliestCompletionFrame.ShouldBe(21L);
        info.ExpiryFrame.ShouldBeGreaterThan(info.EarliestCompletionFrame);
        info.SelectedElementIndices.Span.SequenceEqual(selected).ShouldBeTrue();
        info.Status.ShouldBe(PhysicsChainReadbackStatus.Pending);

        scheduler.ReleaseReadback(handle).ShouldBeFalse();
        scheduler.CancelReadback(handle).ShouldBeTrue();
        info.Status.ShouldBe(PhysicsChainReadbackStatus.Cancelled);
        scheduler.CancelReadback(handle).ShouldBeFalse();
        Destroy(world, node);
    }

    [Test]
    public void DuplicateSameFrameRequestIsCoalesced()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();

        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 1L, out PhysicsChainReadbackHandle first, out _).ShouldBeTrue();
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 1L, out PhysicsChainReadbackHandle second, out _).ShouldBeTrue();

        second.ShouldBe(first);
        Destroy(world, node);
    }

    [Test]
    public void StaleInstanceAndBudgetOverflowAreExplicitlyRejected()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        PhysicsChainRuntimeHandle stale = component.RuntimeHandle;
        Destroy(world, node);

        scheduler.TryRequestReadback(stale, PhysicsChainReadbackFields.Bounds, [0], 24, 1L, out _, out PhysicsChainReadbackRejection staleRejection).ShouldBeFalse();
        staleRejection.ShouldBe(PhysicsChainReadbackRejection.InvalidInstance);
        PhysicsChainReadbackCounters staleCounters = scheduler.GetReadbackCounters();
        staleCounters.Requested.ShouldBe(1L);
        staleCounters.Rejected.ShouldBe(1L);

        var next = CreateRegisteredComponent();
        int[] oversized = new int[PhysicsChainReadbackLimits.Default.MaximumElementsPerFrame + 1];
        int oversizedBytes = checked(oversized.Length * PhysicsChainReadbackLayout.ParticleByteCount);
        next.Scheduler.TryRequestReadback(next.Component.RuntimeHandle, PhysicsChainReadbackFields.Particles, oversized, oversizedBytes, 2L, out _, out PhysicsChainReadbackRejection overflowRejection).ShouldBeFalse();
        overflowRejection.ShouldBe(PhysicsChainReadbackRejection.ElementBudgetExceeded);
        Destroy(next.World, next.Node);
    }

    [Test]
    public void AdvanceFrame_ExpiresAtDeadlineAndTerminalReleaseInvalidatesHandle()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        scheduler.TryRequestReadback(
            component.RuntimeHandle,
            PhysicsChainReadbackFields.Bounds,
            [0],
            24,
            10L,
            out PhysicsChainReadbackHandle expiredHandle,
            out _).ShouldBeTrue();
        scheduler.TryGetReadbackRequest(expiredHandle, out PhysicsChainReadbackRequestInfo? info).ShouldBeTrue();
        info.ShouldNotBeNull();

        scheduler.AdvanceReadbacks(info!.ExpiryFrame - 1L);
        info.Status.ShouldBe(PhysicsChainReadbackStatus.Pending);
        scheduler.AdvanceReadbacks(info.ExpiryFrame);
        info.Status.ShouldBe(PhysicsChainReadbackStatus.Expired);
        info.CompletionFrame.ShouldBe(info.ExpiryFrame);

        PhysicsChainReadbackCounters beforeRelease = scheduler.GetReadbackCounters();
        scheduler.AdvanceReadbacks(info.ExpiryFrame + 1L);
        scheduler.GetReadbackCounters().Expired.ShouldBe(1L);
        beforeRelease.Requested.ShouldBe(1L);
        beforeRelease.Expired.ShouldBe(1L);
        beforeRelease.LiveRequests.ShouldBe(1);

        scheduler.ReleaseReadback(expiredHandle).ShouldBeTrue();
        scheduler.TryGetReadbackRequest(expiredHandle, out _).ShouldBeFalse();
        scheduler.ReleaseReadback(expiredHandle).ShouldBeFalse();
        PhysicsChainReadbackCounters afterRelease = scheduler.GetReadbackCounters();
        afterRelease.Released.ShouldBe(1L);
        afterRelease.LiveRequests.ShouldBe(0);
        Destroy(world, node);
    }

    [Test]
    public void Counters_DistinguishRequestedCoalescedRejectedCancelledAndReleased()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 4L, out PhysicsChainReadbackHandle handle, out _).ShouldBeTrue();
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 4L, out PhysicsChainReadbackHandle duplicate, out _).ShouldBeTrue();
        duplicate.ShouldBe(handle);
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.None, [0], 24, 4L, out _, out PhysicsChainReadbackRejection rejection).ShouldBeFalse();
        rejection.ShouldBe(PhysicsChainReadbackRejection.NoFields);
        scheduler.CancelReadback(handle).ShouldBeTrue();

        PhysicsChainReadbackCounters counters = scheduler.GetReadbackCounters();
        counters.Requested.ShouldBe(3L);
        counters.Coalesced.ShouldBe(1L);
        counters.Rejected.ShouldBe(1L);
        counters.Cancelled.ShouldBe(1L);
        counters.Expired.ShouldBe(0L);
        counters.Released.ShouldBe(0L);
        counters.LiveRequests.ShouldBe(1);

        scheduler.ReleaseReadback(handle).ShouldBeTrue();
        counters = scheduler.GetReadbackCounters();
        counters.Released.ShouldBe(1L);
        counters.LiveRequests.ShouldBe(0);
        Destroy(world, node);
    }

    [Test]
    public void Release_ReusesSlotWithNewGeneration()
    {
        var (world, scheduler, node, component) = CreateRegisteredComponent();
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 6L, out PhysicsChainReadbackHandle stale, out _).ShouldBeTrue();
        scheduler.CancelReadback(stale).ShouldBeTrue();
        scheduler.ReleaseReadback(stale).ShouldBeTrue();
        scheduler.TryRequestReadback(component.RuntimeHandle, PhysicsChainReadbackFields.Bounds, [0], 24, 7L, out PhysicsChainReadbackHandle current, out _).ShouldBeTrue();

        current.Slot.ShouldBe(stale.Slot);
        current.Generation.ShouldNotBe(stale.Generation);
        scheduler.TryGetReadbackRequest(stale, out _).ShouldBeFalse();
        scheduler.TryGetReadbackRequest(current, out _).ShouldBeTrue();
        Destroy(world, node);
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
