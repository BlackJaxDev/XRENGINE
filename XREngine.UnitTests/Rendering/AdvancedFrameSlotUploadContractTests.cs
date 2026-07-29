using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedFrameSlotUploadContractTests
{
    [Test]
    public void PreSizedArenasCoverEveryUploadStreamAndRotateFrameSlots()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 128u,
            overflowBytes: 64u);

        arena.TryBeginFrame(frameOrdinal: 0UL, completedValue: 0UL)
            .ShouldBeTrue();
        arena.CurrentSlot.ShouldBe(0u);
        arena.PreviousSlot.ShouldBe(2u);

        ulong expectedBytes = 0UL;
        for (int i = 0; i < AdvancedFrameUploadCapacityProfile.StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream =
                (EAdvancedFrameUploadStream)i;
            uint byteCount = checked((uint)((i + 1) * 8));
            arena.TryAllocate(stream, byteCount, out AdvancedFrameUploadAllocation allocation)
                .ShouldBeTrue();
            allocation.Stream.ShouldBe(stream);
            allocation.StorageGeneration.ShouldBe(arena.CurrentStorageGeneration);
            allocation.FrameSlot.ShouldBe(0u);
            allocation.IsOverflow.ShouldBeFalse();
            allocation.Span.Fill(checked((byte)(i + 1)));
            expectedBytes += byteCount;
        }

        Span<AdvancedUploadCopyRange> copyPlan =
            stackalloc AdvancedUploadCopyRange[16];
        arena.TryBuildCurrentCopyPlan(copyPlan, out int rangeCount)
            .ShouldBeTrue();
        rangeCount.ShouldBe(AdvancedFrameUploadCapacityProfile.StreamCount);
        for (int i = 0; i < rangeCount; i++)
        {
            copyPlan[i].Stream.ShouldBe((EAdvancedFrameUploadStream)i);
            copyPlan[i].StorageGeneration.ShouldBe(arena.CurrentStorageGeneration);
            copyPlan[i].FrameSlot.ShouldBe(0u);
            copyPlan[i].IsOverflow.ShouldBeFalse();
        }

        AdvancedFrameUploadTelemetrySnapshot telemetry =
            arena.GetTelemetrySnapshot();
        telemetry.BytesWritten.ShouldBe(expectedBytes);
        telemetry.DirtyRangeCount.ShouldBe(rangeCount);
        telemetry.PerSlotCapacityBytes.ShouldBe(128UL * 5UL);
        telemetry.MappedCapacityBytes.ShouldBe(128UL * 5UL * 3UL);

        arena.EndFrame(submissionCompletionValue: 1UL);
        arena.TryBeginFrame(frameOrdinal: 1UL, completedValue: 1UL)
            .ShouldBeTrue();
        arena.CurrentSlot.ShouldBe(1u);
        arena.PreviousSlot.ShouldBe(0u);
        arena.EndFrame(submissionCompletionValue: 2UL);
    }

    [Test]
    public void CapacityGrowthUsesHighWaterOnlyAtAnExplicitFrameBoundary()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 32u,
            overflowBytes: 128u);
        ulong initialGeneration = arena.CurrentStorageGeneration;

        arena.TryBeginFrame(frameOrdinal: 0UL, completedValue: 0UL)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Instance,
                48u,
                out AdvancedFrameUploadAllocation overflow)
            .ShouldBeTrue();
        overflow.IsOverflow.ShouldBeTrue();
        arena.CurrentCapacity.InstanceBytes.ShouldBe(32u);
        arena.CurrentStorageGeneration.ShouldBe(initialGeneration);
        arena.EndFrame(submissionCompletionValue: 5UL);

        arena.CurrentCapacity.InstanceBytes.ShouldBe(32u);
        arena.CurrentStorageGeneration.ShouldBe(initialGeneration);

        arena.TryBeginFrame(frameOrdinal: 1UL, completedValue: 0UL)
            .ShouldBeTrue();
        arena.CurrentCapacity.InstanceBytes.ShouldBe(64u);
        arena.CurrentStorageGeneration.ShouldNotBe(initialGeneration);
        arena.RetiredMainGenerationCount.ShouldBe(1);
        arena.PendingOverflowGenerationCount.ShouldBe(1);

        AdvancedFrameUploadTelemetrySnapshot growth =
            arena.GetTelemetrySnapshot();
        growth.CapacityGrowthCount.ShouldBe(1);
        growth.CapacityGrowthBytes.ShouldBeGreaterThan(0UL);
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Instance,
                48u,
                out AdvancedFrameUploadAllocation primary)
            .ShouldBeTrue();
        primary.IsOverflow.ShouldBeFalse();
        arena.EndFrame(submissionCompletionValue: 6UL);

        arena.TryBeginFrame(frameOrdinal: 2UL, completedValue: 5UL)
            .ShouldBeTrue();
        AdvancedFrameUploadTelemetrySnapshot retired =
            arena.GetTelemetrySnapshot();
        retired.RetiredGenerationCount.ShouldBe(2);
        arena.PendingOverflowGenerationCount.ShouldBe(0);
        arena.RetiredMainGenerationCount.ShouldBe(0);
        arena.EndFrame(submissionCompletionValue: 7UL);
    }

    [Test]
    public void OverflowExhaustionIsVisibleAndNeverWaitsForDeviceCompletion()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 16u,
            overflowBytes: 16u,
            overflowGenerationCount: 1);

        arena.TryBeginFrame(frameOrdinal: 0UL, completedValue: 0UL)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Material,
                16u,
                out AdvancedFrameUploadAllocation primary)
            .ShouldBeTrue();
        primary.IsOverflow.ShouldBeFalse();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Material,
                16u,
                out AdvancedFrameUploadAllocation overflow)
            .ShouldBeTrue();
        overflow.IsOverflow.ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Material,
                1u,
                out _)
            .ShouldBeFalse();

        AdvancedFrameUploadTelemetrySnapshot telemetry =
            arena.GetTelemetrySnapshot();
        telemetry.OverflowAllocationCount.ShouldBe(1);
        telemetry.OverflowBytes.ShouldBe(16UL);
        telemetry.OverflowExhaustionCount.ShouldBe(1);
        arena.EndFrame(submissionCompletionValue: 9UL);
        arena.PendingOverflowGenerationCount.ShouldBe(1);
        arena.AvailableOverflowGenerationCount.ShouldBe(0);

        arena.TryBeginFrame(frameOrdinal: 1UL, completedValue: 8UL)
            .ShouldBeTrue();
        arena.PendingOverflowGenerationCount.ShouldBe(1);
        arena.AvailableOverflowGenerationCount.ShouldBe(0);
        arena.EndFrame(submissionCompletionValue: 10UL);

        arena.TryBeginFrame(frameOrdinal: 2UL, completedValue: 9UL)
            .ShouldBeTrue();
        arena.PendingOverflowGenerationCount.ShouldBe(0);
        arena.AvailableOverflowGenerationCount.ShouldBe(1);
        arena.GetTelemetrySnapshot().RetiredGenerationCount
            .ShouldBeGreaterThanOrEqualTo(1);
        arena.EndFrame(submissionCompletionValue: 11UL);
    }

    [Test]
    public void DirtyRangesCoalesceIntoAFixedCapacityCopyPlan()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 256u,
            overflowBytes: 64u,
            maxDirtyRangesPerStream: 2);

        arena.TryBeginFrame(frameOrdinal: 0UL, completedValue: 0UL)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Instance,
                8u,
                alignmentBytes: 1u,
                out _)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Instance,
                8u,
                alignmentBytes: 64u,
                out _)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.Instance,
                8u,
                alignmentBytes: 64u,
                out _)
            .ShouldBeTrue();
        arena.TryAllocate(
                EAdvancedFrameUploadStream.View,
                4u,
                alignmentBytes: 1u,
                out _)
            .ShouldBeTrue();

        arena.GetCurrentCopyRangeCount().ShouldBe(2);
        Span<AdvancedUploadCopyRange> insufficient =
            stackalloc AdvancedUploadCopyRange[1];
        arena.TryBuildCurrentCopyPlan(insufficient, out int required)
            .ShouldBeFalse();
        required.ShouldBe(2);

        Span<AdvancedUploadCopyRange> plan =
            stackalloc AdvancedUploadCopyRange[2];
        arena.TryBuildCurrentCopyPlan(plan, out int count)
            .ShouldBeTrue();
        count.ShouldBe(2);
        plan[0].Stream.ShouldBe(EAdvancedFrameUploadStream.Instance);
        plan[0].SourceOffsetBytes.ShouldBe(0u);
        plan[0].DestinationOffsetBytes.ShouldBe(0u);
        plan[0].ByteCount.ShouldBe(136u);
        plan[1].Stream.ShouldBe(EAdvancedFrameUploadStream.View);
        plan[1].ByteCount.ShouldBe(4u);
        count.ShouldBeLessThanOrEqualTo(arena.MaxCopyRangeCount);
        arena.EndFrame(submissionCompletionValue: 1UL);
    }

    [Test]
    public void IdenticalLogicalWritesProduceIdenticalBackendNeutralCopyPlans()
    {
        using AdvancedFrameSlotUploadArena openGlConsumer = CreateArena(
            primaryBytes: 128u,
            overflowBytes: 64u);
        using AdvancedFrameSlotUploadArena vulkanConsumer = CreateArena(
            primaryBytes: 128u,
            overflowBytes: 64u);

        openGlConsumer.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        vulkanConsumer.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        for (int i = 0; i < AdvancedFrameUploadCapacityProfile.StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream =
                (EAdvancedFrameUploadStream)i;
            uint byteCount = checked((uint)(16 + (i * 4)));
            openGlConsumer.TryAllocate(stream, byteCount, out _)
                .ShouldBeTrue();
            vulkanConsumer.TryAllocate(stream, byteCount, out _)
                .ShouldBeTrue();
        }

        Span<AdvancedUploadCopyRange> openGlPlan =
            stackalloc AdvancedUploadCopyRange[16];
        Span<AdvancedUploadCopyRange> vulkanPlan =
            stackalloc AdvancedUploadCopyRange[16];
        openGlConsumer.TryBuildCurrentCopyPlan(openGlPlan, out int openGlCount)
            .ShouldBeTrue();
        vulkanConsumer.TryBuildCurrentCopyPlan(vulkanPlan, out int vulkanCount)
            .ShouldBeTrue();

        vulkanCount.ShouldBe(openGlCount);
        for (int i = 0; i < openGlCount; i++)
            vulkanPlan[i].ShouldBe(openGlPlan[i]);

        openGlConsumer.EndFrame(1UL);
        vulkanConsumer.EndFrame(1UL);
    }

    [Test]
    public void InFlightSlotReuseDefersWithoutBlocking()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 64u,
            overflowBytes: 32u);

        arena.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        arena.EndFrame(10UL);
        arena.TryBeginFrame(1UL, 0UL).ShouldBeTrue();
        arena.EndFrame(11UL);
        arena.TryBeginFrame(2UL, 0UL).ShouldBeTrue();
        arena.EndFrame(12UL);

        arena.TryBeginFrame(3UL, 9UL).ShouldBeFalse();
        arena.CurrentSlot.ShouldBe(0u);
        arena.GetTelemetrySnapshot().SlotReuseDeferralCount.ShouldBe(1);

        arena.TryBeginFrame(3UL, 10UL).ShouldBeTrue();
        arena.EndFrame(13UL);
    }

    [Test]
    public void WarmedExtractionAndCopyPlanningAllocateZeroManagedBytes()
    {
        using AdvancedFrameSlotUploadArena arena = CreateArena(
            primaryBytes: 512u,
            overflowBytes: 64u,
            maxDirtyRangesPerStream: 4);
        Span<AdvancedUploadCopyRange> plan =
            stackalloc AdvancedUploadCopyRange[32];
        ulong completed = 0UL;
        ulong frameOrdinal = 0UL;

        for (int warmup = 0; warmup < 32; warmup++)
        {
            RunWarmedFrame(arena, frameOrdinal++, completed, completed + 1UL, plan)
                .ShouldBeTrue();
            completed++;
        }

        bool succeeded = true;
        int copiedRanges = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 512; iteration++)
        {
            succeeded &= RunWarmedFrame(
                arena,
                frameOrdinal++,
                completed,
                completed + 1UL,
                plan);
            completed++;
            copiedRanges += plan[0].ByteCount == 0u ? 0 : 1;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        succeeded.ShouldBeTrue();
        copiedRanges.ShouldBe(512);
        allocated.ShouldBe(0L);
    }

    private static bool RunWarmedFrame(
        AdvancedFrameSlotUploadArena arena,
        ulong frameOrdinal,
        ulong completedValue,
        ulong submissionValue,
        Span<AdvancedUploadCopyRange> plan)
    {
        if (!arena.TryBeginFrame(frameOrdinal, completedValue))
            return false;

        for (int i = 0; i < AdvancedFrameUploadCapacityProfile.StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream =
                (EAdvancedFrameUploadStream)i;
            if (!arena.TryAllocate(stream, 32u, out AdvancedFrameUploadAllocation allocation))
                return false;
            allocation.Span[0] = checked((byte)(i + 1));
        }

        if (!arena.TryBuildCurrentCopyPlan(plan, out int rangeCount) ||
            rangeCount != AdvancedFrameUploadCapacityProfile.StreamCount)
        {
            return false;
        }

        arena.EndFrame(submissionValue);
        return true;
    }

    private static AdvancedFrameSlotUploadArena CreateArena(
        uint primaryBytes,
        uint overflowBytes,
        int maxDirtyRangesPerStream = 4,
        int overflowGenerationCount = 2)
        => new(
            new AdvancedFrameSlotUploadArenaOptions(
                SlotCount: 3u,
                UniformCapacity(primaryBytes),
                UniformCapacity(overflowBytes),
                DefaultAlignmentBytes: 8u,
                maxDirtyRangesPerStream,
                overflowGenerationCount,
                RetiredGenerationCapacity: 2));

    private static AdvancedFrameUploadCapacityProfile UniformCapacity(
        uint byteCount)
        => new(
            byteCount,
            byteCount,
            byteCount,
            byteCount,
            byteCount);
}
