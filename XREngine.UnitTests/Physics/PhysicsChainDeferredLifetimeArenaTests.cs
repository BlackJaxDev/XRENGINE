using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainDeferredLifetimeArenaTests
{
    [Test]
    public void RetiredSlotRemainsReadableAndCannotBeReusedBeforeCompletion()
    {
        var arena = new PhysicsChainDeferredLifetimeArena<string>(1, 2);
        PhysicsChainArenaHandle retired = arena.Allocate("in-flight");
        arena.Retire(retired, 5L).ShouldBeTrue();

        arena.TryGet(retired, out string? value).ShouldBeTrue();
        value.ShouldBe("in-flight");
        PhysicsChainArenaHandle second = arena.Allocate("second");
        second.Slot.ShouldNotBe(retired.Slot);
        arena.TryAllocate("overflow", out _).ShouldBeFalse();
        arena.AdvanceCompletedEpoch(4L).ShouldBe(0);
        arena.TryGet(retired, out _).ShouldBeTrue();

        arena.AdvanceCompletedEpoch(5L).ShouldBe(1);
        arena.TryGet(retired, out _).ShouldBeFalse();
        PhysicsChainArenaHandle reused = arena.Allocate("reused");
        reused.Slot.ShouldBe(retired.Slot);
        reused.Generation.ShouldNotBe(retired.Generation);
        arena.TryGet(second, out value).ShouldBeTrue();
        value.ShouldBe("second");
    }

    [Test]
    public void DuplicateRetirementAndEpochRegressionAreRejected()
    {
        var arena = new PhysicsChainDeferredLifetimeArena<int>();
        PhysicsChainArenaHandle handle = arena.Allocate(1);
        arena.Retire(handle, 3L).ShouldBeTrue();
        arena.Retire(handle, 4L).ShouldBeFalse();
        arena.AdvanceCompletedEpoch(2L).ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => arena.AdvanceCompletedEpoch(1L));
    }
}
