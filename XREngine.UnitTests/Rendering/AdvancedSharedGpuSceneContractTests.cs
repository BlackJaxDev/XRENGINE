using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedSharedGpuSceneContractTests
{
    [Test]
    public void DrawHandle_ResolvesCompleteSceneAndMaterialChainForIndependentConsumers()
    {
        AdvancedSharedGpuSceneDatabase database = CreateDatabase();
        AdvancedGpuHandle material = AddMaterial(database.Materials);
        AdvancedGpuHandle draw = AddDraw(database.Scene, material);

        database.PublishHandleLookups().ShouldBeTrue();
        database.TryResolveDraw(draw, out AdvancedResolvedSharedDrawRecords desktop)
            .ShouldBeTrue();
        database.TryResolveDraw(draw, out AdvancedResolvedSharedDrawRecords eye)
            .ShouldBeTrue();

        desktop.Scene.Draw.Material.ShouldBe(material);
        desktop.Scene.Geometry.IsResident.ShouldBeTrue();
        desktop.Scene.EditorIdentity.StableInstanceId.ShouldBe(0xAABBCCDDul);
        desktop.Material.StableRowId.ShouldBe(material.Index);
        eye.Scene.Draw.ShouldBe(desktop.Scene.Draw);
        eye.Material.ShouldBe(desktop.Material);

        database.TryCreateDrawDependencySnapshot(
                draw,
                out AdvancedSharedDrawDependencySnapshot snapshot)
            .ShouldBeTrue();
        snapshot.Scene.Draw.ShouldBe(draw);
        snapshot.Scene.Material.ShouldBe(material);
        snapshot.MaterialDenseIndex.ShouldBe(0u);

        AdvancedGpuSceneLookupLayout layout = database.HandleLookups.Layout;
        database.HandleLookups.TryResolve(
                draw,
                layout.Draws,
                out uint drawDenseIndex)
            .ShouldBeTrue();
        database.HandleLookups.TryResolve(
                material,
                layout.Materials,
                out uint materialDenseIndex)
            .ShouldBeTrue();
        drawDenseIndex.ShouldBe(snapshot.Scene.DrawDenseIndex);
        materialDenseIndex.ShouldBe(snapshot.MaterialDenseIndex);
    }

    [Test]
    public void LookupPublication_IsAllocationFreeWhenCapacitiesAreStable()
    {
        AdvancedSharedGpuSceneDatabase database = CreateDatabase();
        AdvancedGpuHandle material = AddMaterial(database.Materials);
        AddDraw(database.Scene, material);
        database.PublishHandleLookups().ShouldBeTrue();
        database.HandleLookups.PublishedDirtyRanges.Length.ShouldBeGreaterThan(0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool published = database.PublishHandleLookups();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        published.ShouldBeTrue();
        database.HandleLookups.PublishedDirtyRanges.Length.ShouldBe(0);
        allocated.ShouldBe(0L);
    }

    [Test]
    public void LookupPublication_CopiesOnlyChangedLogicalSegments()
    {
        AdvancedSharedGpuSceneDatabase database = CreateDatabase();
        database.PublishHandleLookups().ShouldBeTrue();

        database.Scene.RenderStates.TryAdd(
                new AdvancedRenderStateRecord { StateClass = 7u },
                out AdvancedGpuHandle renderState)
            .ShouldBeTrue();
        database.PublishHandleLookups().ShouldBeTrue();

        ReadOnlySpan<AdvancedGpuDirtyRange> ranges =
            database.HandleLookups.PublishedDirtyRanges;
        ranges.Length.ShouldBe(1);
        ranges[0].ShouldBe(new AdvancedGpuDirtyRange(
            database.HandleLookups.Layout.RenderStates.Offset +
                renderState.Index,
            1u));
    }

    [Test]
    public void FrameBoundaryLookupGrowth_PreservesExistingRowsAndPublishesFullImage()
    {
        AdvancedSharedGpuSceneDatabase database = CreateDatabase();
        AdvancedGpuHandle material = AddMaterial(database.Materials);
        AdvancedGpuHandle draw = AddDraw(database.Scene, material);
        database.PublishHandleLookups().ShouldBeTrue();

        AdvancedSharedGpuSceneCapacityProfile grown =
            CreateCapacityProfile() with
            {
                Scene = CreateCapacityProfile().Scene with
                {
                    DrawRecords = 16u,
                    InstanceRecords = 16u,
                },
                MaterialRecords = 16u,
            };
        database.GrowAtFrameBoundary(grown);

        database.HandleLookups.PublishedDirtyRanges.Length.ShouldBe(1);
        database.HandleLookups.PublishedDirtyRanges[0].ShouldBe(
            new AdvancedGpuDirtyRange(
                0u,
                database.HandleLookups.Layout.TotalCount));
        database.HandleLookups.TryResolve(
                draw,
                database.HandleLookups.Layout.Draws,
                out _)
            .ShouldBeTrue();
        database.HandleLookups.TryResolve(
                material,
                database.HandleLookups.Layout.Materials,
                out _)
            .ShouldBeTrue();
    }

    [Test]
    public void StaleMaterialGeneration_InvalidatesDrawResolutionAndGpuLookup()
    {
        AdvancedSharedGpuSceneDatabase database = CreateDatabase();
        AdvancedGpuHandle material = AddMaterial(database.Materials);
        AdvancedGpuHandle draw = AddDraw(database.Scene, material);
        database.PublishHandleLookups().ShouldBeTrue();

        database.Materials.RemoveMaterial(material).ShouldBeTrue();
        database.PublishHandleLookups().ShouldBeTrue();

        database.TryResolveDraw(draw, out _).ShouldBeFalse();
        database.HandleLookups.TryResolve(
                material,
                database.HandleLookups.Layout.Materials,
                out _)
            .ShouldBeFalse();
    }

    private static AdvancedSharedGpuSceneDatabase CreateDatabase()
        => new(CreateCapacityProfile());

    private static AdvancedSharedGpuSceneCapacityProfile CreateCapacityProfile()
        => new(
            new AdvancedGpuSceneCapacityProfile(
                DrawRecords: 8u,
                InstanceRecords: 8u,
                TransformRecords: 16u,
                DeformationRecords: 8u,
                RenderStateRecords: 8u,
                EditorIdentityRecords: 8u,
                GeometryRecords: 8u,
                StaticVertexBytes: 4096u,
                IndexBytes: 4096u,
                PreSkinnedCurrentBytes: 4096u,
                PreSkinnedPreviousBytes: 4096u,
                MeshletBytes: 4096u),
            MaterialRecords: 8u,
            ShadingKernels: 4u,
            MaterialLayouts: 4u,
            MaterialLayoutMembers: 8u,
            MaterialConstantWords: 64u,
            MaterialTextureBindings: 16u);

    private static AdvancedGpuHandle AddMaterial(
        AdvancedMaterialDatabase materials)
    {
        AdvancedMaterialLayoutRecord layoutRecord = new()
        {
            LayoutHash = 0x12345678ul,
            ConstantWordCount = 1u,
            TextureReferenceCount = 0u,
            RequiredAttributeMask =
                EAdvancedMaterialRequiredAttributeMask.Position |
                EAdvancedMaterialRequiredAttributeMask.Normal,
        };
        AdvancedMaterialLayoutMember[] layoutMembers =
        [
            new(0xA11ul, EAdvancedMaterialValueKind.Float, 0u, 1u),
        ];
        materials.TryAddLayout(
                layoutRecord,
                layoutMembers,
                out AdvancedGpuHandle layout)
            .ShouldBeTrue();

        AdvancedShadingKernelRecord kernelRecord = new()
        {
            SupportedCoverageMask =
                1u << (int)EAdvancedMaterialCoverageMode.Opaque,
            SupportedEligibility =
                EAdvancedMaterialEligibilityFlags.NativeOpaque,
            SupportedFeatures =
                EAdvancedMaterialFeatureFlags.ReceivesShadows,
            ShaderIdentityHash = 0xF00Dul,
        };
        materials.TryAddKernel(
                layout,
                kernelRecord,
                out AdvancedGpuHandle kernel)
            .ShouldBeTrue();

        AdvancedMaterialRecord materialRecord = new()
        {
            RenderStateClass =
                EAdvancedMaterialRenderStateClass.OpaqueSingleSided,
            CoverageMode = EAdvancedMaterialCoverageMode.Opaque,
            FeatureFlags = EAdvancedMaterialFeatureFlags.ReceivesShadows,
            EligibilityFlags = EAdvancedMaterialEligibilityFlags.NativeOpaque,
        };
        materials.TryAddMaterial(
                layout,
                kernel,
                materialRecord,
                [new AdvancedMaterialValueDescriptor(
                    0xA11ul,
                    EAdvancedMaterialValueKind.Float,
                    1u)],
                [BitConverter.SingleToUInt32Bits(1.0f)],
                ReadOnlySpan<AdvancedMaterialTextureBinding>.Empty,
                out AdvancedGpuHandle material)
            .ShouldBeTrue();
        return material;
    }

    private static AdvancedGpuHandle AddDraw(
        AdvancedGpuSceneDatabase scene,
        AdvancedGpuHandle material)
    {
        scene.Geometry.TryAddStatic(
                AdvancedGeometryDatabaseContractTests.CreateTriangleVertices(),
                [0u, 1u, 2u],
                AdvancedGeometryDatabaseContractTests.CreateTriangleRegistration(),
                out AdvancedGpuHandle geometry)
            .ShouldBeTrue();
        scene.Instances.TryAdd(
                new AdvancedInstanceRecord
                {
                    CurrentWorld = Matrix4x4.Identity,
                    PreviousWorld = Matrix4x4.Identity,
                    BoundsSphere = new Vector4(0f, 0f, 0f, 1f),
                    VisibilityFlags = EAdvancedInstanceVisibilityFlags.Enabled,
                    CurrentFrameSlot = 1u,
                    PreviousFrameSlot = 0u,
                },
                out AdvancedGpuHandle instance)
            .ShouldBeTrue();
        scene.Transforms.TryAdd(
                new AdvancedTransformRecord
                {
                    World = Matrix4x4.Identity,
                    FrameSlot = 1u,
                },
                out AdvancedGpuHandle currentTransform)
            .ShouldBeTrue();
        scene.Transforms.TryAdd(
                new AdvancedTransformRecord
                {
                    World = Matrix4x4.Identity,
                    FrameSlot = 0u,
                },
                out AdvancedGpuHandle previousTransform)
            .ShouldBeTrue();
        scene.RenderStates.TryAdd(
                new AdvancedRenderStateRecord { StateClass = 1u },
                out AdvancedGpuHandle renderState)
            .ShouldBeTrue();
        scene.EditorIdentities.TryAdd(
                new AdvancedEditorIdentityRecord
                {
                    StableInstanceId = 0xAABBCCDDul,
                    SelectionId = 17u,
                },
                out AdvancedGpuHandle editorIdentity)
            .ShouldBeTrue();

        scene.Draws.TryAdd(
                new AdvancedDrawRecord
                {
                    Instance = instance,
                    Geometry = geometry,
                    Material = material,
                    RenderState = renderState,
                    EditorIdentity = editorIdentity,
                    CurrentTransform = currentTransform,
                    PreviousTransform = previousTransform,
                },
                out AdvancedGpuHandle draw)
            .ShouldBeTrue();
        return draw;
    }
}
