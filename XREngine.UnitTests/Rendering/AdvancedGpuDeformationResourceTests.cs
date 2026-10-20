using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

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

    [Test]
    public void GenerationOfDestroyedSceneIsReleasedAtNextSelection()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        GPUScene first = new();
        GPUScene second = new();

        resources.TrySelectStaticGeneration(first, 1UL, 1UL).ShouldBeTrue();
        resources.TrySelectStaticGeneration(second, 1UL, 1UL).ShouldBeTrue();
        resources.ReleasedStaticGenerationCount.ShouldBe(0u);

        // Play transitions destroy the world's GPU scene; its generation must not
        // keep its capacity and meshes until it happens to be reassigned.
        first.Destroy();
        resources.TrySelectStaticGeneration(second, 1UL, 1UL).ShouldBeTrue();
        resources.ReleasedStaticGenerationCount.ShouldBe(1u);

        GPUScene third = new();
        resources.TrySelectStaticGeneration(third, 1UL, 1UL).ShouldBeTrue();
        second.Destroy();
        third.Destroy();
    }

    [Test]
    public void MeshSliceRevision_AdvancesWhenAFrameOpensOrTheStaticGenerationResets()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        ulong initial = resources.MeshSliceRevision;

        resources.TryBeginFrame(0UL, 0UL, 0u, 2u, 16u).ShouldBeTrue();
        ulong opened = resources.MeshSliceRevision;
        opened.ShouldBeGreaterThan(initial);
        resources.Publish([], [], []);
        resources.EndFrame(0UL);

        resources.TryResetStaticGenerationAtBoundary().ShouldBeTrue();
        resources.MeshSliceRevision.ShouldBeGreaterThan(opened);
    }

    [Test]
    public void UnsupportedMesh_KeepsTheMeshSliceRevision()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        resources.TryBeginFrame(0UL, 0UL, 0u, 2u, 16u).ShouldBeTrue();
        ulong revision = resources.MeshSliceRevision;

        resources.TryPrepareMesh(new XRMesh(), 1u, out _)
            .ShouldBe(AdvancedGpuDeformationMeshPreparationStatus.Unsupported);

        resources.MeshSliceRevision.ShouldBe(revision);
        resources.Publish([], [], []);
        resources.EndFrame(0UL);
    }

    [Test]
    public void PreparedMesh_IsReusableOnlyForTheSameRevisionAndTopology()
    {
        XRMesh mesh = new();
        AdvancedGpuDeformationMeshSlice slice = new(
            SourceVertexOffset: 4u, BoneInfluenceOffset: 4u, BlendshapeRangeOffset: 0u,
            VertexCount: (uint)mesh.VertexCount, BlendshapeCount: 0u, TopologyGeneration: 7u);
        AdvancedPreparedDeformationMesh prepared = new(slice, SliceRevision: 3UL,
            mesh.GeometryRevision, Ready: true);

        prepared.IsReusable(3UL, mesh, 7u).ShouldBeTrue();
        prepared.IsReusable(4UL, mesh, 7u).ShouldBeFalse();
        prepared.IsReusable(3UL, mesh, 8u).ShouldBeFalse();
        (prepared with { Ready = false }).IsReusable(3UL, mesh, 7u).ShouldBeFalse();
        (prepared with { GeometryRevision = mesh.GeometryRevision + 1 }).IsReusable(3UL, mesh, 7u)
            .ShouldBeFalse();
        (prepared with { Slice = slice with { VertexCount = slice.VertexCount + 1u } })
            .IsReusable(3UL, mesh, 7u).ShouldBeFalse();
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
