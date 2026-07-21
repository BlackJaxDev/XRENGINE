using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainArenaCapacityTests
{
    [Test]
    public void CapacityOverflowIsVisibleAndDoesNotCorruptLiveState()
    {
        var arena = new PhysicsChainSlotArena<int>(1, maximumCapacity: 2);
        PhysicsChainArenaHandle first = arena.Allocate(10);
        PhysicsChainArenaHandle second = arena.Allocate(20);

        arena.TryAllocate(30, out PhysicsChainArenaHandle rejected).ShouldBeFalse();
        rejected.ShouldBe(PhysicsChainArenaHandle.Invalid);
        Should.Throw<PhysicsChainArenaCapacityException>(() => arena.Allocate(30));
        arena.TryGet(first, out int firstValue).ShouldBeTrue();
        arena.TryGet(second, out int secondValue).ShouldBeTrue();
        firstValue.ShouldBe(10);
        secondValue.ShouldBe(20);
    }

    [Test]
    public void FragmentationRecommendationIsExplicitAndNeverMovesLiveSlots()
    {
        var arena = new PhysicsChainSlotArena<int>(4)
        {
            FragmentationRebuildThreshold = 0.4f,
        };
        PhysicsChainArenaHandle first = arena.Allocate(1);
        PhysicsChainArenaHandle second = arena.Allocate(2);
        PhysicsChainArenaHandle third = arena.Allocate(3);
        arena.Free(first).ShouldBeTrue();
        arena.ShouldRecommendRebuild.ShouldBeFalse();
        arena.Free(second).ShouldBeTrue();
        arena.ShouldRecommendRebuild.ShouldBeTrue();

        arena.TryGet(third, out int value).ShouldBeTrue();
        value.ShouldBe(3);
    }
}
