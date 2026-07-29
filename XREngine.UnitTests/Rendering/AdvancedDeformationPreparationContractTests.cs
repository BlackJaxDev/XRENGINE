using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedDeformationPreparationContractTests
{
    [Test]
    public void PackedLayoutsAndReferenceOrderAreDeterministic()
    {
        Unsafe.SizeOf<AdvancedDeformedVertex>().ShouldBe(64);
        Unsafe.SizeOf<AdvancedDeformationJobRecord>().ShouldBe(112);
        Unsafe.SizeOf<AdvancedSkinInfluence>().ShouldBe(48);
        Unsafe.SizeOf<AdvancedSpillInfluence>().ShouldBe(8);
        Unsafe.SizeOf<AdvancedActiveBlendshape>().ShouldBe(8);
        Unsafe.SizeOf<AdvancedBlendshapeRange>().ShouldBe(16);
        Unsafe.SizeOf<AdvancedBlendshapeSparseRecord>().ShouldBe(16);

        AdvancedReferenceVertex source = new(
            Position: new Vector3(1.0f, 0.0f, 0.0f),
            Normal: Vector3.UnitY,
            Tangent: Vector3.UnitX);
        AdvancedReferenceBlendshape[] blendshapes =
        [
            new(
                PositionDelta: new Vector3(1.0f, 0.0f, 0.0f),
                NormalDelta: Vector3.Zero,
                TangentDelta: Vector3.Zero,
                Weight: 1.0f),
        ];
        AdvancedReferenceBoneInfluence[] influences =
        [
            new(0u, 1.0f),
        ];
        Matrix4x4[] animatedPose =
        [
            Matrix4x4.CreateScale(2.0f),
        ];

        AdvancedReferenceVertex output =
            AdvancedDeformationReference.Deform(
                source,
                blendshapes,
                influences,
                animatedPose);
        output.Position.X.ShouldBe(4.0f, 0.0001f);
        output.Normal.ShouldBe(Vector3.UnitY);
        output.Tangent.ShouldBe(Vector3.UnitX);
    }

    [TestCase(1.0f)]
    [TestCase(-1.0f)]
    public void PackedTangentPreservesDirectionAndBitangentSign(
        float bitangentSign)
    {
        Vector3 tangent = Vector3.Normalize(
            new Vector3(-0.35f, -0.72f, 0.6f));
        uint packed = AdvancedPackedVertexCodec.EncodeTangentOct(
            tangent,
            bitangentSign);
        Vector3 decoded =
            AdvancedPackedVertexCodec.DecodeTangentOct(
                packed,
                out float decodedSign);

        Vector3.Dot(tangent, decoded).ShouldBeGreaterThan(0.999f);
        decodedSign.ShouldBe(bitangentSign);
    }

    [Test]
    public void StaticAnimationBlendshapeIkAndPhysicsOutputsAreDeterministic()
    {
        AdvancedReferenceVertex source = new(
            Position: new Vector3(1.0f, 2.0f, 3.0f),
            Normal: Vector3.UnitY,
            Tangent: Vector3.UnitX);

        AdvancedDeformationReference.Deform(
                source,
                [],
                [],
                [])
            .ShouldBe(source);

        AdvancedReferenceBoneInfluence[] singleBone =
        [
            new(BoneIndex: 0u, Weight: 1.0f),
        ];
        AdvancedReferenceVertex animated =
            AdvancedDeformationReference.Deform(
                source,
                [],
                singleBone,
                [Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f)]);
        animated.Position.ShouldBe(new Vector3(3.0f, 2.0f, 3.0f));

        AdvancedReferenceVertex blendshape =
            AdvancedDeformationReference.Deform(
                source,
                [
                    new AdvancedReferenceBlendshape(
                        PositionDelta: new Vector3(2.0f, 0.0f, 0.0f),
                        NormalDelta: Vector3.Zero,
                        TangentDelta: Vector3.Zero,
                        Weight: 0.5f),
                ],
                singleBone,
                [Matrix4x4.Identity]);
        blendshape.Position.ShouldBe(new Vector3(2.0f, 2.0f, 3.0f));

        // IK and physics-chain solvers both publish final palette matrices.
        // Their outputs therefore use the same aggregate deformation path.
        AdvancedReferenceVertex solved =
            AdvancedDeformationReference.Deform(
                source,
                [],
                [
                    new AdvancedReferenceBoneInfluence(0u, 0.5f),
                    new AdvancedReferenceBoneInfluence(1u, 0.5f),
                ],
                [
                    Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f),
                    Matrix4x4.CreateTranslation(0.0f, 0.0f, 4.0f),
                ]);
        solved.Position.ShouldBe(new Vector3(1.0f, 3.0f, 5.0f));
    }

    [Test]
    public void CurrentPreviousArenaHandlesLodTopologyAndNewVisibility()
    {
        AdvancedDeformedVertexArena arena = new(
            new AdvancedDeformedVertexArenaOptions(
                InitialVertexCapacity: 32u,
                FrameSlotCount: 3,
                OwnerCapacity: 8,
                RetiredGenerationCapacity: 2));
        AdvancedGpuHandle owner = new(1u, 1u);

        arena.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                owner,
                vertexCount: 4u,
                topologyGeneration: 1u,
                lodGeneration: 1u,
                newlyVisible: true,
                out AdvancedDeformedArenaSlice first)
            .ShouldBeTrue();
        first.VelocityValidity.ShouldBe(
            EAdvancedVelocityValidityReason.NewlyVisible);
        arena.GetCurrentVertices(first)[0].Position =
            new Vector3(1.0f, 2.0f, 3.0f);
        arena.EndFrame(1UL);

        arena.TryBeginFrame(1UL, 1UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                owner,
                4u,
                topologyGeneration: 1u,
                lodGeneration: 1u,
                newlyVisible: false,
                out AdvancedDeformedArenaSlice animated)
            .ShouldBeTrue();
        animated.HasValidVelocity.ShouldBeTrue();
        animated.CurrentVertexOffset.ShouldBe(first.CurrentVertexOffset);
        arena.GetPreviousVertices(animated)[0].Position
            .ShouldBe(new Vector3(1.0f, 2.0f, 3.0f));
        arena.EndFrame(2UL);

        arena.TryBeginFrame(2UL, 2UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                owner,
                4u,
                topologyGeneration: 1u,
                lodGeneration: 2u,
                newlyVisible: false,
                out AdvancedDeformedArenaSlice lodChanged)
            .ShouldBeTrue();
        lodChanged.HasValidVelocity.ShouldBeTrue();
        lodChanged.CurrentVertexOffset.ShouldBe(
            animated.CurrentVertexOffset);
        arena.EndFrame(3UL);

        arena.TryBeginFrame(3UL, 3UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                owner,
                6u,
                topologyGeneration: 2u,
                lodGeneration: 3u,
                newlyVisible: false,
                out AdvancedDeformedArenaSlice topologyChanged)
            .ShouldBeTrue();
        topologyChanged.VelocityValidity.ShouldBe(
            EAdvancedVelocityValidityReason.TopologyChanged);
        topologyChanged.PreviousVertexOffset.ShouldBe(
            lodChanged.CurrentVertexOffset);
        topologyChanged.CurrentVertexOffset.ShouldNotBe(
            topologyChanged.PreviousVertexOffset);
        arena.EndFrame(4UL);

        arena.TryBeginFrame(4UL, 4UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                owner,
                6u,
                topologyGeneration: 2u,
                lodGeneration: 3u,
                newlyVisible: true,
                out AdvancedDeformedArenaSlice reappeared)
            .ShouldBeTrue();
        reappeared.VelocityValidity.ShouldBe(
            EAdvancedVelocityValidityReason.NewlyVisible);
        arena.EndFrame(5UL);
    }

    [Test]
    public void ArenaGrowthOccursAtBoundaryAndRetiresByCompletion()
    {
        AdvancedDeformedVertexArena arena = new(
            new AdvancedDeformedVertexArenaOptions(
                InitialVertexCapacity: 4u,
                FrameSlotCount: 3,
                OwnerCapacity: 4,
                RetiredGenerationCapacity: 2));

        arena.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        arena.TryAcquireSlice(
                new AdvancedGpuHandle(1u, 1u),
                vertexCount: 8u,
                topologyGeneration: 1u,
                lodGeneration: 1u,
                newlyVisible: true,
                out _)
            .ShouldBeFalse();
        arena.VertexCapacity.ShouldBe(4u);
        arena.EndFrame(5UL);

        arena.TryBeginFrame(1UL, 0UL).ShouldBeTrue();
        arena.VertexCapacity.ShouldBeGreaterThanOrEqualTo(8u);
        arena.GetTelemetry().CapacityGrowthCount.ShouldBe(1u);
        arena.GetTelemetry().RetiredGenerationCount.ShouldBe(1);
        arena.EndFrame(6UL);

        arena.TryBeginFrame(2UL, 5UL).ShouldBeTrue();
        arena.GetTelemetry().RetiredGenerationCount.ShouldBe(0);
        arena.EndFrame(7UL);
    }

    [Test]
    public void JobStreamDeduplicatesAndAdmitsWholeHighestValueJobs()
    {
        AdvancedDeformationJobStream stream = new(capacity: 8);
        stream.BeginFrame();
        AdvancedDeformationCandidate mandatory =
            CreateCandidate(
                id: 1u,
                vertices: 40u,
                contribution: 0.1f,
                mandatory: true);
        AdvancedDeformationCandidate optionalLow =
            CreateCandidate(
                id: 2u,
                vertices: 40u,
                contribution: 0.2f,
                mandatory: false);
        AdvancedDeformationCandidate optionalHigh =
            CreateCandidate(
                id: 3u,
                vertices: 40u,
                contribution: 0.9f,
                mandatory: false);

        stream.TryAdd(mandatory, out int first).ShouldBeTrue();
        stream.TryAdd(mandatory, out int duplicate).ShouldBeTrue();
        duplicate.ShouldBe(first);
        AdvancedDeformationCandidate newGeneration =
            mandatory with
            {
                Key = mandatory.Key with { PoseGeneration = 2u },
                Job = mandatory.Job with { PoseGeneration = 2u },
            };
        stream.TryAdd(newGeneration, out int distinct).ShouldBeTrue();
        distinct.ShouldNotBe(first);
        stream.TryAdd(optionalLow, out int lowIndex).ShouldBeTrue();
        stream.TryAdd(optionalHigh, out int highIndex).ShouldBeTrue();

        AdvancedDeformationAdmissionResult result = stream.FinalizeJobs(
            new AdvancedDeformationBudget(
                MaximumJobs: 3u,
                MaximumVertices: 120UL,
                MaximumOutputBytes: 120UL * 64UL,
                EAdvancedDeformationOverflowBehavior.CpuDirectDiagnostic));

        result.DeduplicatedCount.ShouldBe(1u);
        result.AdmittedJobCount.ShouldBe(3u);
        result.RejectedJobCount.ShouldBe(1u);
        result.BudgetExceeded.ShouldBeTrue();
        stream.Jobs[0].CurrentVertexOffset.ShouldBe(100u);
        stream.Jobs[1].CurrentVertexOffset.ShouldBe(100u);
        stream.Jobs[2].CurrentVertexOffset.ShouldBe(300u);
        stream.IsCandidateAdmitted(first).ShouldBeTrue();
        stream.IsCandidateAdmitted(distinct).ShouldBeTrue();
        stream.IsCandidateAdmitted(lowIndex).ShouldBeFalse();
        stream.IsCandidateAdmitted(highIndex).ShouldBeTrue();
        foreach (AdvancedDeformationJobRecord job in stream.Jobs)
            job.CurrentVertexOffset.ShouldNotBe(200u);
    }

    [Test]
    public void FinalizedJobsUploadAsOneFrameSlotStream()
    {
        AdvancedDeformationJobStream stream = new(capacity: 2);
        stream.BeginFrame();
        AdvancedDeformationJobRecord expected =
            CreateCandidate(1u, 64u, 1.0f, true).Job;
        stream.TryAdd(
                CreateCandidate(1u, 64u, 1.0f, true),
                out _)
            .ShouldBeTrue();
        stream.FinalizeJobs(new AdvancedDeformationBudget(
            MaximumJobs: 2u,
            MaximumVertices: 128UL,
            MaximumOutputBytes: 128UL * 64UL,
            EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity));

        AdvancedFrameUploadCapacityProfile capacity = new(
            InstanceBytes: 64u,
            ViewBytes: 64u,
            DeformationJobBytes: 512u,
            LightBytes: 64u,
            MaterialBytes: 64u);
        using AdvancedFrameSlotUploadArena arena = new(
            new AdvancedFrameSlotUploadArenaOptions(
                SlotCount: 3u,
                InitialCapacity: capacity,
                OverflowCapacity: capacity,
                DefaultAlignmentBytes: 16u,
                MaxDirtyRangesPerStream: 2,
                OverflowGenerationCount: 1,
                RetiredGenerationCapacity: 1));
        arena.TryBeginFrame(0UL, 0UL).ShouldBeTrue();
        stream.TryUpload(
                arena,
                out AdvancedFrameUploadAllocation allocation)
            .ShouldBeTrue();

        allocation.Stream.ShouldBe(
            EAdvancedFrameUploadStream.DeformationJob);
        allocation.ByteCount.ShouldBe(
            checked((uint)Unsafe.SizeOf<AdvancedDeformationJobRecord>()));
        MemoryMarshal.Read<AdvancedDeformationJobRecord>(allocation.Span)
            .ShouldBe(expected);
        Span<AdvancedUploadCopyRange> copies =
            stackalloc AdvancedUploadCopyRange[arena.MaxCopyRangeCount];
        arena.TryBuildCurrentCopyPlan(copies, out int copyCount)
            .ShouldBeTrue();
        copyCount.ShouldBe(1);
        copies[0].Stream.ShouldBe(
            EAdvancedFrameUploadStream.DeformationJob);
        arena.EndFrame(1UL);
    }

    [Test]
    public void CompatibleMeshesUseOneDispatchAndEveryConsumerGetsABarrier()
    {
        AdvancedDeformationJobRecord[] jobs = new AdvancedDeformationJobRecord[128];
        for (int i = 0; i < jobs.Length; i++)
            jobs[i] = CreateCandidate(
                checked((uint)i + 1u),
                vertices: 1_000u,
                contribution: 1.0f,
                mandatory: true).Job;

        AdvancedDeformationDispatchPlanner planner = new(
            maximumJobs: 128,
            maximumFamilies: 4);
        planner.Build(jobs).ShouldBeTrue();
        planner.Batches.Length.ShouldBe(1);
        planner.Batches[0].JobCount.ShouldBe(128u);
        planner.JobVertexOffsets[127].ShouldBe(127_000u);

        AdvancedDeformationDispatchBackendProbe vulkan = new(
            RuntimeGraphicsApiKind.Vulkan)
        {
            LastGpuMilliseconds = 0.42,
        };
        AdvancedDeformationDispatchTelemetry telemetry =
            new AdvancedDeformationExecutor().Execute(
                planner,
                vulkan,
                jobs,
                EAdvancedPreparationConsumer.Visibility |
                EAdvancedPreparationConsumer.Velocity |
                EAdvancedPreparationConsumer.MaterialReconstruction |
                EAdvancedPreparationConsumer.DirectionalShadow,
                EAdvancedDeformationExecutionMode.AggregateCompute,
                admissionOverflowCount: 0u);

        telemetry.DispatchCount.ShouldBe(1u);
        telemetry.JobCount.ShouldBe(128u);
        telemetry.VertexCount.ShouldBe(128_000UL);
        telemetry.GpuMilliseconds.ShouldBe(0.42);
        vulkan.DispatchCount.ShouldBe(1);
        vulkan.BarrierCount.ShouldBe(4);
    }

    [Test]
    public void RuntimeFeatureFlagsRemainOneCanonicalDispatchFamily()
    {
        AdvancedDeformationJobRecord[] jobs =
        [
            CreateCandidate(1u, 64u, 1.0f, true).Job with
            {
                VertexLayoutId = AdvancedDeformedVertex.CanonicalLayoutId,
                Features =
                    EAdvancedDeformationFeatureFlags.Skinning |
                    EAdvancedDeformationFeatureFlags.Normals,
            },
            CreateCandidate(2u, 96u, 1.0f, true).Job with
            {
                VertexLayoutId = AdvancedDeformedVertex.CanonicalLayoutId,
                Features =
                    EAdvancedDeformationFeatureFlags.Skinning |
                    EAdvancedDeformationFeatureFlags.Normals |
                    EAdvancedDeformationFeatureFlags.Tangents |
                    EAdvancedDeformationFeatureFlags.Blendshapes |
                    EAdvancedDeformationFeatureFlags.SpillInfluences |
                    EAdvancedDeformationFeatureFlags.PrecomposedPalette,
            },
        ];
        AdvancedDeformationDispatchPlanner planner = new(2, 1);

        planner.Build(jobs).ShouldBeTrue();
        planner.FamilyOverflowCount.ShouldBe(0u);
        planner.Batches.Length.ShouldBe(1);
        planner.Batches[0].JobCount.ShouldBe(2u);
        planner.Batches[0].VertexCount.ShouldBe(160UL);
    }

    [Test]
    public void MissingVulkanAggregateSupportNeverSilentlyFallsBack()
    {
        AdvancedDeformationJobRecord[] jobs =
        [
            CreateCandidate(1u, 4u, 1.0f, true).Job,
        ];
        AdvancedDeformationDispatchPlanner planner = new(1, 1);
        planner.Build(jobs).ShouldBeTrue();
        AdvancedDeformationDispatchBackendProbe unsupported = new(
            RuntimeGraphicsApiKind.Vulkan,
            supportsAggregateCompute: false);
        AdvancedDeformationExecutor executor = new();

        Should.Throw<NotSupportedException>(() => executor.Execute(
            planner,
            unsupported,
            jobs,
            EAdvancedPreparationConsumer.Visibility,
            EAdvancedDeformationExecutionMode.AggregateCompute,
            admissionOverflowCount: 0u));

        executor.Execute(
                planner,
                unsupported,
                jobs,
                EAdvancedPreparationConsumer.Visibility,
                EAdvancedDeformationExecutionMode.DirectVertexDiagnostic,
                admissionOverflowCount: 0u)
            .DispatchCount.ShouldBe(0u);
    }

    [Test]
    public void WarmedJobAndDispatchPlanningAllocatesZeroManagedBytes()
    {
        AdvancedDeformationJobStream stream = new(capacity: 32);
        AdvancedDeformationDispatchPlanner planner = new(32, 4);
        PopulateAndPlan(stream, planner);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PopulateAndPlan(stream, planner);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0L);
        planner.Batches.Length.ShouldBe(1);
    }

    private static void PopulateAndPlan(
        AdvancedDeformationJobStream stream,
        AdvancedDeformationDispatchPlanner planner)
    {
        stream.BeginFrame();
        for (uint i = 0u; i < 32u; i++)
            stream.TryAdd(
                CreateCandidate(i + 1u, 100u, 1.0f, true),
                out _);
        stream.FinalizeJobs(new AdvancedDeformationBudget(
            MaximumJobs: 32u,
            MaximumVertices: 3_200UL,
            MaximumOutputBytes: 3_200UL * 64UL,
            EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity));
        planner.Build(stream.Jobs);
    }

    private static AdvancedDeformationCandidate CreateCandidate(
        uint id,
        uint vertices,
        float contribution,
        bool mandatory)
    {
        AdvancedGpuHandle mesh = new(id, 1u);
        AdvancedGpuHandle pose = new(id, 1u);
        AdvancedDeformationJobRecord job = new()
        {
            Mesh = mesh,
            SharedPose = pose,
            CurrentVertexOffset = id * 100u,
            PreviousVertexOffset = id * 100u,
            VertexCount = vertices,
            BoneCount = 64u,
            MeshGeneration = 1u,
            PoseGeneration = 1u,
            PaletteGeneration = 1u,
            TopologyGeneration = 1u,
            VertexLayoutId = 0xAA55UL,
            Features =
                EAdvancedDeformationFeatureFlags.Skinning |
                EAdvancedDeformationFeatureFlags.Normals,
            Precision = EAdvancedDeformationPrecision.Packed,
            Order = EAdvancedDeformationOrder.BlendshapeThenSkinning,
            OutputStride = 64u,
        };
        return new AdvancedDeformationCandidate(
            job,
            new AdvancedDeformationJobKey(
                mesh,
                pose,
                job.MeshGeneration,
                job.PoseGeneration,
                job.PaletteGeneration,
                job.TopologyGeneration,
                job.VertexLayoutId,
                job.Features,
                job.Precision),
            contribution,
            mandatory,
            Visible: true);
    }
}
