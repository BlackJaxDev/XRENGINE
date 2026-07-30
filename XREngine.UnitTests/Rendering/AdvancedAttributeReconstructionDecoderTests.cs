using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedAttributeReconstructionDecoderTests
{
    [Test]
    public void Decoder_DistinguishesMissingFromKnownNonresidentGeometry()
    {
        AdvancedSharedGpuSceneDatabase missingDatabase =
            new(CreateCapacity());
        AdvancedGpuHandle missingGeometry = new(1u, 1u);
        AdvancedVisibilityEncodedSurface missingSurface =
            CreateEncodedSurface(missingDatabase, missingGeometry);
        AdvancedReconstructionDecoder.TryResolve(
                missingSurface,
                missingDatabase,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out EAdvancedReconstructionInvalidReason missingReason)
            .ShouldBeFalse();
        missingReason.ShouldBe(
            EAdvancedReconstructionInvalidReason.GeometryMissing);

        AdvancedSharedGpuSceneDatabase nonresidentDatabase =
            new(CreateCapacity());
        AdvancedGeometryRegistration registration =
            AdvancedGeometryRegistration.Create(
                vertexCount: 3u,
                indexCount: 3u,
                vertexStride: 64u,
                EPrimitiveType.Triangles,
                vertexLayoutId: AdvancedDeformedVertex.CanonicalLayoutId,
                boundsSphere: new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                boundsMin: new Vector4(-1.0f),
                boundsMax: new Vector4(1.0f));
        nonresidentDatabase.Scene.Geometry.TryAddMissing(
                registration,
                AdvancedGpuHandle.Invalid,
                out AdvancedGpuHandle nonresidentGeometry)
            .ShouldBeTrue();
        AdvancedVisibilityEncodedSurface nonresidentSurface =
            CreateEncodedSurface(
                nonresidentDatabase,
                nonresidentGeometry);
        AdvancedReconstructionDecoder.TryResolve(
                nonresidentSurface,
                nonresidentDatabase,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out EAdvancedReconstructionInvalidReason nonresidentReason)
            .ShouldBeFalse();
        nonresidentReason.ShouldBe(
            EAdvancedReconstructionInvalidReason.GeometryNonResident);
    }

    [Test]
    public void Decoder_UsesResidentFallbackBeforeFollowingMaterialState()
    {
        AdvancedSharedGpuSceneDatabase database =
            new(CreateCapacity());
        AdvancedGeometryRegistration registration =
            AdvancedGeometryRegistration.Create(
                vertexCount: 3u,
                indexCount: 3u,
                vertexStride: 64u,
                EPrimitiveType.Triangles,
                vertexLayoutId: AdvancedDeformedVertex.CanonicalLayoutId,
                boundsSphere: new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                boundsMin: new Vector4(-1.0f),
                boundsMax: new Vector4(1.0f));
        database.Scene.Geometry.TryAddStatic(
                new byte[3 * 64],
                new uint[] { 0u, 1u, 2u },
                registration,
                out AdvancedGpuHandle fallback)
            .ShouldBeTrue();
        database.Scene.Geometry.TryAddMissing(
                registration,
                fallback,
                out AdvancedGpuHandle missing)
            .ShouldBeTrue();
        AdvancedVisibilityEncodedSurface surface =
            CreateEncodedSurface(database, missing);

        AdvancedReconstructionDecoder.TryResolve(
                surface,
                database,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out EAdvancedReconstructionInvalidReason reason)
            .ShouldBeFalse();
        reason.ShouldBe(
            EAdvancedReconstructionInvalidReason.MaterialNotResident);
    }

    private static AdvancedVisibilityEncodedSurface CreateEncodedSurface(
        AdvancedSharedGpuSceneDatabase database,
        AdvancedGpuHandle geometry)
    {
        database.Scene.Instances.TryAdd(
                default,
                out AdvancedGpuHandle instance)
            .ShouldBeTrue();
        database.Scene.Transforms.TryAdd(
                default,
                out AdvancedGpuHandle transform)
            .ShouldBeTrue();
        AdvancedGpuHandle material = new(1u, 1u);
        database.Scene.Draws.TryAdd(
                new AdvancedDrawRecord
                {
                    Instance = instance,
                    Geometry = geometry,
                    Material = material,
                    CurrentTransform = transform,
                    PreviousTransform = transform,
                },
                out AdvancedGpuHandle draw)
            .ShouldBeTrue();
        database.PublishHandleLookups().ShouldBeTrue();

        AdvancedVisibilityPayload payload = new(
            draw,
            geometry,
            material,
            default,
            PrimitiveSection: 0u,
            InstanceCount: 1u,
            FirstIndex: 0u,
            IndexCount: 3u,
            VertexCount: 3u,
            RasterStateClass: 0u,
            Coverage: EAdvancedMaterialCoverageMode.Opaque,
            CullMode: 0u,
            PrimitiveTopology: (uint)EPrimitiveType.Triangles,
            Skinned: false,
            MeshletsResident: false,
            ForceCpuDiagnostic: false);
        AdvancedVisibilityProducerEncoder.TryEncode(
                payload,
                new AdvancedVisibilityPrimitiveReference(0u, 0u, 0u),
                EAdvancedGeometryProducer.IndirectIndexed,
                EAdvancedVisibilityRasterOrigin.Early,
                viewIndex: 0u,
                selectionId: 0u,
                frontFace: true,
                velocityValid: false,
                out AdvancedVisibilityEncodedSurface encoded,
                out _)
            .ShouldBeTrue();
        return encoded;
    }

    private static AdvancedSharedGpuSceneCapacityProfile CreateCapacity()
        => new(
            new AdvancedGpuSceneCapacityProfile(
                DrawRecords: 4u,
                InstanceRecords: 4u,
                TransformRecords: 4u,
                DeformationRecords: 4u,
                RenderStateRecords: 4u,
                EditorIdentityRecords: 4u,
                GeometryRecords: 4u,
                StaticVertexBytes: 4096u,
                IndexBytes: 4096u,
                PreSkinnedCurrentBytes: 4096u,
                PreSkinnedPreviousBytes: 4096u,
                MeshletBytes: 4096u),
            MaterialRecords: 4u,
            ShadingKernels: 4u,
            MaterialLayouts: 4u,
            MaterialLayoutMembers: 32u,
            MaterialConstantWords: 32u,
            MaterialTextureBindings: 32u);
}
