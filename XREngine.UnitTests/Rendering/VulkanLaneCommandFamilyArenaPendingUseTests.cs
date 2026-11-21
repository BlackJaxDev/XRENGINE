using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanLaneCommandFamilyArenaPendingUseTests
{
    [Test]
    public void PendingUse_HasOneOwnerAndOnlyThatOwnerReleasesIt()
    {
        VulkanLaneCommandFamilyArena arena = CreateArena();

        arena.AcquirePendingUse(7L);
        arena.HasPendingUse.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => arena.AcquirePendingUse(8L));
        Should.Throw<InvalidOperationException>(() => arena.ReleasePendingUse(8L));

        arena.ReleasePendingUse(7L);
        arena.HasPendingUse.ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => arena.AcquirePendingUse(0L));
    }

    [Test]
    public void PendingUse_BlocksRetirementUntilReleased()
    {
        VulkanLaneCommandFamilyArena arena = CreateArena();
        arena.AcquirePendingUse(3L);

        Should.Throw<InvalidOperationException>(() => arena.ClearAfterRetirement());

        arena.ReleasePendingUse(3L);
        arena.ClearAfterRetirement();
        arena.TransientPool.Handle.ShouldBe(0UL);
    }

    private static VulkanLaneCommandFamilyArena CreateArena()
        => new(laneId: 2, frameSlot: 0, queueFamilyIndex: 0, new CommandPool(11UL), new CommandPool(12UL));
}
