using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Execution;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class VulkanPendingDesktopRecordTests
{
    [Test]
    public void Record_HoldsTheArenaFromPreparationUntilAcceptedSubmission()
    {
        VulkanLaneCommandFamilyArena arena = CreateArena(laneId: 2);
        var record = new VulkanPendingDesktopRecord();
        using var domain = new RenderWorkDomain(backgroundWorkerCount: 2, ERenderWorkerQos.OsDefault);
        RenderWorkBatchLease lease = domain.RentBatch(1);

        record.BeginPreparation(arena, laneId: 2, schedulerFrameSlot: 0, frameDataSlot: 1);
        arena.HasPendingUse.ShouldBeTrue();
        arena.PendingUseOwnerGeneration.ShouldBe(record.OwnerGeneration);

        record.MarkSubmitted(lease);
        Should.Throw<InvalidOperationException>(() => record.Discard());
        record.MarkJoined(new RenderWorkBatchResult(lease.Generation, RenderWorkBatchStatus.Completed, 1, null));

        // The join does not end the pending use; only the accepted submission does.
        arena.HasPendingUse.ShouldBeTrue();
        record.CompleteAfterAcceptedSubmission();
        arena.HasPendingUse.ShouldBeFalse();
        record.HasUnresolvedWork.ShouldBeFalse();
        lease.Dispose();
    }

    [Test]
    public void Record_DiscardsUnsuccessfulOutputAndRejectsMismatchedArenas()
    {
        VulkanLaneCommandFamilyArena arena = CreateArena(laneId: 2);
        var record = new VulkanPendingDesktopRecord();

        Should.Throw<ArgumentException>(() =>
            record.BeginPreparation(arena, laneId: 1, schedulerFrameSlot: 0, frameDataSlot: 0));
        arena.HasPendingUse.ShouldBeFalse();

        record.BeginPreparation(arena, laneId: 2, schedulerFrameSlot: 0, frameDataSlot: 0);
        record.Discard();
        arena.HasPendingUse.ShouldBeFalse();
        record.Phase.ShouldBe(EVulkanPendingDesktopRecordPhase.Idle);

        // A second record cannot take an arena that another owner still holds.
        var other = new VulkanPendingDesktopRecord();
        record.BeginPreparation(arena, laneId: 2, schedulerFrameSlot: 0, frameDataSlot: 0);
        Should.Throw<InvalidOperationException>(() =>
            other.BeginPreparation(arena, laneId: 2, schedulerFrameSlot: 0, frameDataSlot: 0));
        record.Discard();
    }

    private static VulkanLaneCommandFamilyArena CreateArena(int laneId)
        => new(laneId, frameSlot: 0, queueFamilyIndex: 0, new CommandPool(21UL), new CommandPool(22UL));
}
