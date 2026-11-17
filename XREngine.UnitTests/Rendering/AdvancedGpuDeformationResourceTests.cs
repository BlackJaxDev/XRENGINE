using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.UnitTests.VR;

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
        // Output becomes history only after its producer work is submitted
        // with a completion fence.
        RecordSubmittedProducerOutput(resources, slot: 0u);
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
    public void OutputSlotSelection_SkipsTheBusyOldestSlotAndKeepsTheLastFrameAsPrevious()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        OpenAndCloseFrame(resources, frameId: 7UL, currentFrameSlot: 2u, previousFrameSlot: 1u);
        SetSlotProducer(resources, slot: 0u, producerFrameId: 5UL, EGpuFenceStatus.Pending);
        SetSlotProducer(resources, slot: 1u, producerFrameId: 6UL, EGpuFenceStatus.Signaled);

        resources.TrySelectOutputSlot(out uint current, out uint previous).ShouldBeTrue();

        // Slot 2 holds the last frame's history, slot 0 is older but still pending.
        current.ShouldBe(1u);
        previous.ShouldBe(2u);
    }

    [Test]
    public void OutputSlotSelection_PrefersTheOldestFreeSlot()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        OpenAndCloseFrame(resources, frameId: 10UL, currentFrameSlot: 2u, previousFrameSlot: 1u);
        SetSlotProducer(resources, slot: 0u, producerFrameId: 9UL, EGpuFenceStatus.Signaled);
        SetSlotProducer(resources, slot: 1u, producerFrameId: 4UL, EGpuFenceStatus.Signaled);

        resources.TrySelectOutputSlot(out uint current, out uint previous).ShouldBeTrue();

        current.ShouldBe(1u);
        previous.ShouldBe(2u);
    }

    [Test]
    public void OutputSlotSelection_DefersWhenEveryCandidateIsBusy()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        OpenAndCloseFrame(resources, frameId: 12UL, currentFrameSlot: 2u, previousFrameSlot: 1u);
        SetSlotProducer(resources, slot: 0u, producerFrameId: 10UL, EGpuFenceStatus.Pending);
        SetSlotProducer(resources, slot: 1u, producerFrameId: 11UL, EGpuFenceStatus.Pending);

        resources.TrySelectOutputSlot(out _, out _).ShouldBeFalse();

        resources.LastBeginFrameDeferral.ShouldBe(EAdvancedDeformationSlotDeferral.PendingCompletion);
        resources.LastOutputReuseSlot.ShouldBe(0u);
        resources.GetBeginFrameDeferralCount(EAdvancedDeformationSlotDeferral.PendingCompletion).ShouldBe(1L);
    }

    [Test]
    public void OutputSlotSelection_UsesUnusedSlotsBeforeTheFirstFrame()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));

        resources.TrySelectOutputSlot(out uint current, out uint previous).ShouldBeTrue();

        current.ShouldBe(0u);
        previous.ShouldNotBe(current);
    }

    [Test]
    public void PoisonedSlot_IsReleasedByALaterSignaledProducerFence()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        OpenAndCloseFrame(resources, frameId: 8UL, currentFrameSlot: 2u, previousFrameSlot: 1u);
        PoisonSlot(resources, slot: 0u, poisonFrameId: 5UL);
        SetSlotProducer(resources, slot: 1u, producerFrameId: 6UL, EGpuFenceStatus.Signaled);

        resources.TrySelectOutputSlot(out uint current, out _).ShouldBeTrue();

        current.ShouldBe(0u);
        resources.SlotPoisonRecoveryCount.ShouldBe(1L);
        resources.PoisonedSlotMask.ShouldBe(0u);
    }

    [Test]
    public void PoisonedSlot_StaysPoisonedWithoutALaterCompletedFence()
    {
        using AdvancedGpuDeformationResources resources =
            new(CreateOptions(initialVertexCapacity: 16u));
        OpenAndCloseFrame(resources, frameId: 8UL, currentFrameSlot: 2u, previousFrameSlot: 1u);
        PoisonSlot(resources, slot: 0u, poisonFrameId: 5UL);
        SetSlotProducer(resources, slot: 1u, producerFrameId: 6UL, EGpuFenceStatus.Pending);

        resources.TrySelectOutputSlot(out _, out _).ShouldBeFalse();

        resources.LastBeginFrameDeferral.ShouldBe(EAdvancedDeformationSlotDeferral.SlotPoisoned);
        resources.SlotPoisonRecoveryCount.ShouldBe(0L);
        resources.PoisonedSlotMask.ShouldBe(1u);
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

    private static void OpenAndCloseFrame(
        AdvancedGpuDeformationResources resources,
        ulong frameId,
        uint currentFrameSlot,
        uint previousFrameSlot)
    {
        resources.TryBeginFrame(
                frameId,
                completedValue: 0UL,
                currentFrameSlot,
                previousFrameSlot,
                requiredOutputVertexCapacity: 16u)
            .ShouldBeTrue();
        resources.EndFrame(frameId);
    }

    private static void PoisonSlot(AdvancedGpuDeformationResources resources, uint slot, ulong poisonFrameId)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(AdvancedGpuDeformationResources);
        ((bool[])type.GetField("_slotReusePoisoned", flags)!.GetValue(resources)!)[slot] = true;
        ((ulong[])type.GetField("_slotPoisonFrameIds", flags)!.GetValue(resources)!)[slot] = poisonFrameId;
    }

    /// <summary>
    /// Gives <paramref name="slot"/> a submitted producer fence in <paramref name="status"/> and
    /// the frame id of the preparation that produced it, as a dispatch would.
    /// </summary>
    private static void SetSlotProducer(
        AdvancedGpuDeformationResources resources,
        uint slot,
        ulong producerFrameId,
        EGpuFenceStatus status)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(AdvancedGpuDeformationResources);
        XRGpuFence?[] fences = (XRGpuFence?[])type.GetField("_slotProducerFences", flags)!.GetValue(resources)!;
        ulong[] producerFrames = (ulong[])type.GetField("_slotProducerFrameIds", flags)!.GetValue(resources)!;
        fences[slot] = new SpectatorTestFence
        {
            Status = status,
            Submission = EGpuFenceSubmissionStatus.Submitted,
        };
        producerFrames[slot] = producerFrameId;
    }

    /// <summary>
    /// Records the state that a successful aggregate dispatch leaves on its
    /// output slot: a submitted producer fence, valid output, and a pin on the
    /// static source generation. A unit test has no renderer to run the
    /// dispatch, so the test sets this state directly.
    /// </summary>
    private static void RecordSubmittedProducerOutput(
        AdvancedGpuDeformationResources resources,
        uint slot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(AdvancedGpuDeformationResources);
        XRGpuFence?[] fences = (XRGpuFence?[])type.GetField("_slotProducerFences", flags)!.GetValue(resources)!;
        bool[] outputValid = (bool[])type.GetField("_slotOutputValid", flags)!.GetValue(resources)!;
        fences[slot].ShouldBeNull();
        fences[slot] = new SpectatorTestFence
        {
            Status = EGpuFenceStatus.Signaled,
            Submission = EGpuFenceSubmissionStatus.Submitted,
        };
        outputValid[slot] = true;
        type.GetMethod("PinCurrentStaticGeneration", flags)!.Invoke(resources, [slot]);
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
