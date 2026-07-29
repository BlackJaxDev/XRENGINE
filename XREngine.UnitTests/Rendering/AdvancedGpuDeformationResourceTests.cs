using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedGpuDeformationResourceTests
{
    [Test]
    public void PublicationRotatesCurrentAndPreviousGpuOutputSlots()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));

        resources.TryBeginFrame(
                frameId: 0UL,
                completedValue: 0UL,
                currentFrameSlot: 0u,
                previousFrameSlot: 2u,
                requiredOutputVertexCapacity: 16u)
            .ShouldBeTrue();
        resources.Publish([], [], []);
        AdvancedGpuDeformationPublication first = resources.Publication;
        first.CurrentFrameSlot.ShouldBe(0u);
        first.PreviousFrameSlot.ShouldBe(2u);
        first.PreviousOutputValid.ShouldBeFalse();
        first.CurrentVertices.AttributeName.ShouldBe(
            "AdvancedDeformation.Output.Slot0");
        first.PreviousVertices.AttributeName.ShouldBe(
            "AdvancedDeformation.Output.Slot2");
        first.JobCount.ShouldBe(0u);
        resources.EndFrame(0UL);

        resources.TryBeginFrame(
                frameId: 1UL,
                completedValue: 0UL,
                currentFrameSlot: 1u,
                previousFrameSlot: 0u,
                requiredOutputVertexCapacity: 16u)
            .ShouldBeTrue();
        resources.Publish([], [], []);
        AdvancedGpuDeformationPublication second = resources.Publication;
        second.CurrentFrameSlot.ShouldBe(1u);
        second.PreviousFrameSlot.ShouldBe(0u);
        second.PreviousOutputValid.ShouldBeTrue();
        second.CurrentVertices.AttributeName.ShouldBe(
            "AdvancedDeformation.Output.Slot1");
        second.PreviousVertices.ShouldBeSameAs(first.CurrentVertices);
        resources.EndFrame(1UL);
    }

    [Test]
    public void OutputGrowthIsBoundaryOwnedAndInvalidatesVelocity()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 4u));

        resources.TryBeginFrame(
                frameId: 3UL,
                completedValue: 2UL,
                currentFrameSlot: 0u,
                previousFrameSlot: 2u,
                requiredOutputVertexCapacity: 17u)
            .ShouldBeTrue();
        resources.OutputCapacityGrowthCount.ShouldBe(1u);
        resources.PreviousOutputValid.ShouldBeFalse();
        resources.Publish([], [], []);
        resources.Publication.CurrentVertices.ElementCount
            .ShouldBeGreaterThanOrEqualTo(17u);
        resources.EndFrame(3UL);
    }

    private static AdvancedPreparationOptions CreateOptions(
        uint initialVertexCapacity)
    {
        AdvancedFrameUploadCapacityProfile uploadCapacity = new(
            InstanceBytes: 64u,
            ViewBytes: 64u,
            DeformationJobBytes: 512u,
            LightBytes: 64u,
            MaterialBytes: 64u);
        return new AdvancedPreparationOptions(
            MaximumDraws: 8,
            MaximumDeformationJobs: 4,
            MaximumDeformationFamilies: 2,
            MaximumIndirectRanges: 4,
            MaximumViews: 2,
            DeformedArena: new AdvancedDeformedVertexArenaOptions(
                initialVertexCapacity,
                FrameSlotCount: 3,
                OwnerCapacity: 4,
                RetiredGenerationCapacity: 2),
            DeformationBudget: new AdvancedDeformationBudget(
                MaximumJobs: 4u,
                MaximumVertices: 1_024UL,
                MaximumOutputBytes: 65_536UL,
                EAdvancedDeformationOverflowBehavior.CpuDirectDiagnostic),
            FrameUploadArena: new AdvancedFrameSlotUploadArenaOptions(
                SlotCount: 3u,
                InitialCapacity: uploadCapacity,
                OverflowCapacity: uploadCapacity,
                DefaultAlignmentBytes: 16u,
                MaxDirtyRangesPerStream: 2,
                OverflowGenerationCount: 1,
                RetiredGenerationCapacity: 1));
    }
}
