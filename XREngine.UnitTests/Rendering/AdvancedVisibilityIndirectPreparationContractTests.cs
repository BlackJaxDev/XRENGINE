using System.Numerics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedVisibilityIndirectPreparationContractTests
{
    [Test]
    public void VisibilityGpuRowsMatchStd430ArrayStrides()
    {
        Unsafe.SizeOf<AdvancedVisibilityCandidate>().ShouldBe(80);
        Unsafe.SizeOf<AdvancedVisibilityPersistentRecord>().ShouldBe(32);
        Unsafe.SizeOf<AdvancedVisibilityPayload>().ShouldBe(96);

        AdvancedVisibilityPayload payload = CreatePayload(
            draw: 1u,
            material: 2u,
            skinned: true,
            meshlets: true);
        payload.Skinned.ShouldBeTrue();
        payload.MeshletsResident.ShouldBeTrue();
        payload.ForceCpuDiagnostic.ShouldBeFalse();
    }

    [Test]
    public void EarlyLatePlanKeepsCountsOnGpuAndRecoversDeferredCandidates()
    {
        AdvancedVisibilityPlanner planner = new(
            maximumViews: 2,
            drawCapacity: 8);
        AdvancedGpuHandle draw = new(1u, 1u);
        AdvancedVisibilityCandidate[] candidates =
        [
            new(
                draw,
                new Vector4(0.0f, 0.0f, 2.0f, 1.0f),
                new Vector4(-1.0f),
                new Vector4(1.0f),
                ViewMask: 1UL,
                BvhLeaf: 7u,
                EAdvancedVisibilityPreparationFlags.Uncertain),
        ];
        planner.BeginFrame(10UL);
        AdvancedVisibilityDispatchPlan plan = planner.BuildPlan(
            viewSlot: 0,
            new AdvancedDepthPyramidContract(
                ViewHistoryKey: 99UL,
                Width: 1920u,
                Height: 1080u,
                MipCount: 11u,
                CurrentGeneration: 2u,
                PreviousGeneration: 1u,
                PreviousValid: false),
            candidates,
            earlyIndirectArgumentOffset: 0u,
            deferredCandidateOffset: 64u,
            lateIndirectArgumentOffset: 128u,
            persistentStateOffset: 0u,
            gpuCounterOffset: 0u);

        plan.RequiresCpuCount.ShouldBeFalse();
        plan.RequiresReadback.ShouldBeFalse();
        plan.LateTestsDeferredOnly.ShouldBeTrue();
        plan.UsesPreviousDepthPyramid.ShouldBeFalse();
        planner.SynchronousReadbackCount.ShouldBe(0UL);
        (planner.GetCandidatePreparationFlags(1)[0] &
            EAdvancedVisibilityPreparationFlags.ConservativeVisible)
            .ShouldBe(
                EAdvancedVisibilityPreparationFlags.ConservativeVisible);

        planner.ApplyGpuResultsForValidation(
            0,
            [
                new AdvancedGpuVisibilityResult(
                    draw,
                    EAdvancedVisibilityPreparationFlags.LateVisible),
            ],
            currentDepthPyramidGeneration: 2u);
        planner.TryGetPersistentRecord(0, draw, out var persistent)
            .ShouldBeTrue();
        persistent.LastVisibleFrame.ShouldBe(10UL);
        persistent.Flags.ShouldBe(
            EAdvancedVisibilityPreparationFlags.LateVisible);

        planner.BeginFrame(11UL);
        AdvancedVisibilityCandidate[] stableCandidates =
        [
            candidates[0] with
            {
                Flags = EAdvancedVisibilityPreparationFlags.None,
            },
        ];
        AdvancedVisibilityDispatchPlan next = planner.BuildPlan(
            0,
            new AdvancedDepthPyramidContract(
                99UL,
                1920u,
                1080u,
                11u,
                CurrentGeneration: 3u,
                PreviousGeneration: 2u,
                PreviousValid: true),
            stableCandidates,
            0u,
            64u,
            128u,
            0u,
            0u);
        next.UsesPreviousDepthPyramid.ShouldBeTrue();
        (planner.GetCandidatePreparationFlags(1)[0] &
            EAdvancedVisibilityPreparationFlags.ConservativeVisible)
            .ShouldBe(EAdvancedVisibilityPreparationFlags.None);
    }

    [Test]
    public void ResizeAndGenerationReplacementAreConservative()
    {
        AdvancedVisibilityPlanner planner = new(1, 4);
        AdvancedVisibilityCandidate candidate = CreateCandidate(
            new AdvancedGpuHandle(1u, 1u));
        planner.BeginFrame(1UL);
        planner.BuildPlan(
            0,
            CreateDepth(5UL, 800u, 600u, previousValid: true),
            [candidate],
            0u,
            0u,
            0u,
            0u,
            0u);

        planner.BeginFrame(2UL);
        planner.BuildPlan(
            0,
            CreateDepth(5UL, 1024u, 768u, previousValid: true),
            [candidate with
            {
                Draw = new AdvancedGpuHandle(1u, 2u),
            }],
            0u,
            0u,
            0u,
            0u,
            0u);

        EAdvancedVisibilityPreparationFlags flags =
            planner.GetCandidatePreparationFlags(1)[0];
        (flags & EAdvancedVisibilityPreparationFlags.ResizedView)
            .ShouldBe(EAdvancedVisibilityPreparationFlags.ResizedView);
        (flags & EAdvancedVisibilityPreparationFlags.NewRecord)
            .ShouldBe(EAdvancedVisibilityPreparationFlags.NewRecord);
        (flags & EAdvancedVisibilityPreparationFlags.ConservativeVisible)
            .ShouldBe(
                EAdvancedVisibilityPreparationFlags.ConservativeVisible);
    }

    [Test]
    public void MixedStaticAndSkinnedMeshletsSharePayloadWithoutSceneRejection()
    {
        AdvancedVisibilityPayload[] payloads =
        [
            CreatePayload(
                draw: 1u,
                material: 10u,
                skinned: false,
                meshlets: true),
            CreatePayload(
                draw: 2u,
                material: 20u,
                skinned: false,
                meshlets: true),
            CreatePayload(
                draw: 3u,
                material: 30u,
                skinned: true,
                meshlets: true),
            CreatePayload(
                draw: 4u,
                material: 40u,
                skinned: true,
                meshlets: false),
        ];
        AdvancedIndirectRangePlanner planner = new(
            maximumPayloads: 8,
            maximumRanges: 8);
        AdvancedIndirectPreparationResult first = planner.Build(
            payloads,
            argumentBufferBase: 128u,
            countBufferBase: 32u,
            argumentStride: 20u,
            countStride: 4u);

        first.StaticMeshletCount.ShouldBe(2u);
        first.SkinnedMeshletCount.ShouldBe(1u);
        first.TraditionalFallbackCount.ShouldBe(1u);
        first.RangeCount.ShouldBe(3u);
        first.RequiresCpuCount.ShouldBeFalse();
        first.RequiresPrimaryRerecord.ShouldBeTrue();
        bool hasSkinnedMeshlets = false;
        bool hasStaticMeshlets = false;
        bool hasTraditionalFallback = false;
        foreach (EAdvancedGeometryProducer producer in planner.Producers)
        {
            hasSkinnedMeshlets |=
                producer == EAdvancedGeometryProducer.SkinnedMeshlet;
            hasStaticMeshlets |=
                producer == EAdvancedGeometryProducer.StaticMeshlet;
            hasTraditionalFallback |=
                producer == EAdvancedGeometryProducer.TraditionalIndirect;
        }
        hasSkinnedMeshlets.ShouldBeTrue();
        hasStaticMeshlets.ShouldBeTrue();
        hasTraditionalFallback.ShouldBeTrue();

        // Material identity differs, but the two static rows share one range.
        int staticRangeCount = 0;
        AdvancedIndirectRange staticRange = default;
        foreach (AdvancedIndirectRange range in planner.Ranges)
        {
            if (range.Key.Producer !=
                EAdvancedGeometryProducer.StaticMeshlet)
                continue;

            staticRangeCount++;
            staticRange = range;
        }
        staticRangeCount.ShouldBe(1);
        staticRange.PayloadCapacity.ShouldBe(2u);

        AdvancedIndirectPreparationResult reused = planner.Build(
            payloads,
            128u,
            32u,
            20u,
            4u);
        reused.StructuralGeneration.ShouldBe(first.StructuralGeneration);
        reused.RequiresPrimaryRerecord.ShouldBeFalse();
    }

    [Test]
    public void CpuDiagnosticAndSecondaryConsumersKeepOneGeometryArchitecture()
    {
        AdvancedVisibilityPayload payload = CreatePayload(
            draw: 1u,
            material: 9u,
            skinned: true,
            meshlets: true);
        payload = payload with
        {
            Flags = payload.Flags |
                EAdvancedVisibilityPayloadFlags.ForceCpuDiagnostic,
        };
        AdvancedIndirectRangePlanner.ResolveProducer(payload)
            .ShouldBe(EAdvancedGeometryProducer.CpuDirectDiagnostic);
        payload.Draw.ShouldBe(new AdvancedGpuHandle(1u, 1u));
        payload.GeometryOffsets.VertexOffset.ShouldBe(100u);

        AdvancedSecondaryGeometryPolicy shadow =
            AdvancedSecondaryGeometryPolicyResolver.Resolve(
                EAdvancedPreparationConsumer.DirectionalShadow,
                EAdvancedMaterialCoverageMode.Masked,
                displacementChangesVisibility: true,
                compatiblePrimaryViewContract: false,
                requiresVelocity: false,
                requiresTemporalHistory: false);
        shadow.ReuseAggregateDeformation.ShouldBeTrue();
        shadow.RequiresIndependentFrustum.ShouldBeTrue();
        shadow.EvaluateCoverageMaterial.ShouldBeTrue();
        shadow.EvaluateDisplacementMaterial.ShouldBeTrue();
        shadow.PreviousDataPolicy.ShouldBe(
            EAdvancedCapturePreviousDataPolicy.NotRequired);

        AdvancedSecondaryGeometryPolicy capture =
            AdvancedSecondaryGeometryPolicyResolver.Resolve(
                EAdvancedPreparationConsumer.Capture,
                EAdvancedMaterialCoverageMode.Opaque,
                displacementChangesVisibility: false,
                compatiblePrimaryViewContract: true,
                requiresVelocity: false,
                requiresTemporalHistory: true);
        capture.ReusePrimaryRelevance.ShouldBeTrue();
        capture.EvaluateCoverageMaterial.ShouldBeFalse();
        capture.PreviousDataPolicy.ShouldBe(
            EAdvancedCapturePreviousDataPolicy.RequiredForTemporalHistory);
    }

    private static AdvancedVisibilityCandidate CreateCandidate(
        AdvancedGpuHandle draw)
        => new(
            draw,
            new Vector4(0.0f, 0.0f, 1.0f, 0.5f),
            new Vector4(-0.5f),
            new Vector4(0.5f),
            1UL,
            0u,
            EAdvancedVisibilityPreparationFlags.None);

    private static AdvancedDepthPyramidContract CreateDepth(
        ulong history,
        uint width,
        uint height,
        bool previousValid)
        => new(
            history,
            width,
            height,
            10u,
            2u,
            1u,
            previousValid);

    private static AdvancedVisibilityPayload CreatePayload(
        uint draw,
        uint material,
        bool skinned,
        bool meshlets)
        => new(
            new AdvancedGpuHandle(draw, 1u),
            new AdvancedGpuHandle(draw, 1u),
            new AdvancedGpuHandle(material, 1u),
            new AdvancedSceneGeometryOffsets(
                VertexOffset: 100u,
                PreviousVertexOffset: 200u,
                IndexOffset: 300u,
                WeightOffset: 400u,
                PaletteOffset: 500u,
                MeshletOffset: 600u,
                MeshletCount: meshlets ? 4u : 0u),
            PrimitiveSection: 0u,
            InstanceCount: 1u,
            FirstIndex: 300u,
            IndexCount: 36u,
            VertexCount: 24u,
            RasterStateClass: 1u,
            Coverage: EAdvancedMaterialCoverageMode.Opaque,
            CullMode: 1u,
            PrimitiveTopology: 4u,
            Skinned: skinned,
            MeshletsResident: meshlets,
            ForceCpuDiagnostic: false);
}
