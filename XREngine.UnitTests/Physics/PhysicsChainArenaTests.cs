using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainArenaTests
{
    [Test]
    public void FreeAndReuseRejectsThePreviousGeneration()
    {
        var arena = new PhysicsChainSlotArena<string>(1);
        PhysicsChainArenaHandle stale = arena.Allocate("first");

        arena.Free(stale).ShouldBeTrue();
        arena.IsCurrent(stale).ShouldBeFalse();
        arena.TryGet(stale, out _).ShouldBeFalse();
        arena.TrySet(stale, "stale").ShouldBeFalse();

        PhysicsChainArenaHandle current = arena.Allocate("second");
        current.Slot.ShouldBe(stale.Slot);
        current.Generation.ShouldNotBe(stale.Generation);
        arena.TryGet(current, out string? value).ShouldBeTrue();
        value.ShouldBe("second");
    }

    [Test]
    public void GeometricGrowthPreservesLiveState()
    {
        var arena = new PhysicsChainSlotArena<int>(1);
        PhysicsChainArenaHandle first = arena.Allocate(10);
        PhysicsChainArenaHandle second = arena.Allocate(20);
        PhysicsChainArenaHandle third = arena.Allocate(30);

        arena.Capacity.ShouldBe(4);
        arena.GrowthCount.ShouldBe(2);
        arena.TryGet(first, out int firstValue).ShouldBeTrue();
        arena.TryGet(second, out int secondValue).ShouldBeTrue();
        arena.TryGet(third, out int thirdValue).ShouldBeTrue();
        firstValue.ShouldBe(10);
        secondValue.ShouldBe(20);
        thirdValue.ShouldBe(30);

        arena.GetReference(second) = 25;
        arena.TryGet(second, out secondValue).ShouldBeTrue();
        secondValue.ShouldBe(25);
    }

    [Test]
    public void SnapshotSeparatesCapacityLiveUseAndFragmentation()
    {
        var arena = new PhysicsChainSlotArena<int>(4);
        PhysicsChainArenaHandle first = arena.Allocate(1);
        _ = arena.Allocate(2);
        _ = arena.Allocate(3);
        arena.Free(first).ShouldBeTrue();

        PhysicsChainArenaSnapshot snapshot = arena.GetSnapshot();
        snapshot.Capacity.ShouldBe(4);
        snapshot.LiveCount.ShouldBe(2);
        snapshot.FreeSlotCount.ShouldBe(1);
        snapshot.GrowthCount.ShouldBe(0);
        snapshot.FragmentationRatio.ShouldBe(1.0f / 3.0f, 0.0001f);
    }

    [Test]
    public void InvalidAndDoubleFreeAreExplicitlyRejected()
    {
        var arena = new PhysicsChainSlotArena<int>();
        arena.Free(PhysicsChainArenaHandle.Invalid).ShouldBeFalse();

        PhysicsChainArenaHandle handle = arena.Allocate(1);
        arena.Free(handle).ShouldBeTrue();
        arena.Free(handle).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => arena.GetReference(handle));
    }
}
