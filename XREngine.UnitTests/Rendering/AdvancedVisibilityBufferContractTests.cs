using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedVisibilityBufferContractTests
{
    [Test]
    public void FormatDecision_PreservesRequiredRangesAndUsesExplicitSentinels()
    {
        AdvancedVisibilityFormatDecision.Selected.ShouldBe(
            EAdvancedVisibilityFormatCandidate.OneRg32UIntAttachment);
        AdvancedVisibilityBufferContract.Encoding.ShouldBe(
            EAdvancedVisibilityTargetEncoding.R32G32UInt);
        AdvancedVisibilityBufferContract.PayloadVersion.ShouldBe(1u);
        AdvancedVisibilityCapacityInventory.TargetScenes.FitsVersion1
            .ShouldBeTrue();
        Unsafe.SizeOf<AdvancedVisibilityPayloadWords>().ShouldBe(8);
        Unsafe.SizeOf<AdvancedVisibilityMetadataWord>().ShouldBe(4);
        Unsafe.SizeOf<AdvancedVisibilityEncodedSurface>().ShouldBe(16);
        Unsafe.SizeOf<AdvancedVisibilityGpuCounters>().ShouldBe(64);

        ReadOnlySpan<AdvancedVisibilityFormatCandidate> inventory =
            AdvancedVisibilityFormatDecision.Inventory;
        inventory.Length.ShouldBe(4);
        inventory[1].CoreOpenGl46.ShouldBeTrue();
        inventory[1].CoreVulkan.ShouldBeTrue();
        inventory[1].PreservesFullDrawAndPrimitiveRange.ShouldBeTrue();
        inventory[2].CoreOpenGl46.ShouldBeFalse();

        AdvancedVisibilityPayloadWords.Invalid.DrawTableIndex.ShouldBe(
            uint.MaxValue);
        AdvancedVisibilityClearContract.IdentityPrimitive.ShouldBe(
            uint.MaxValue);
        AdvancedVisibilityClearContract.Depth(reversedDepth: false)
            .ShouldBe(1.0f);
        AdvancedVisibilityClearContract.Depth(reversedDepth: true)
            .ShouldBe(0.0f);
    }

    [Test]
    public void PayloadEncoding_RejectsInvalidAndOverflowingFieldsWithoutWrapping()
    {
        AdvancedVisibilityBufferContract.TryEncodeIdentity(
                AdvancedGpuHandle.Invalid,
                1u,
                out AdvancedVisibilityPayloadWords invalid,
                out EAdvancedVisibilityPayloadOverflow invalidReason)
            .ShouldBeFalse();
        invalid.ShouldBe(AdvancedVisibilityPayloadWords.Invalid);
        invalidReason.ShouldBe(
            EAdvancedVisibilityPayloadOverflow.InvalidDraw);

        AdvancedVisibilityBufferContract.TryEncodeIdentity(
                new AdvancedGpuHandle(1u, 1u),
                uint.MaxValue,
                out AdvancedVisibilityPayloadWords overflow,
                out EAdvancedVisibilityPayloadOverflow overflowReason)
            .ShouldBeFalse();
        overflow.ShouldBe(AdvancedVisibilityPayloadWords.Invalid);
        overflowReason.ShouldBe(
            EAdvancedVisibilityPayloadOverflow.PrimitiveIndex);
        Should.Throw<AdvancedVisibilityPayloadOverflowException>(() =>
            AdvancedVisibilityBufferContract.EncodeIdentityOrThrow(
                new AdvancedGpuHandle(1u, 1u),
                uint.MaxValue));
    }

    [Test]
    public void MetadataAndPrimitiveWords_RoundTripEveryProducerSemantic()
    {
        AdvancedVisibilityMetadataWord.TryCreate(
                EAdvancedGeometryProducer.CpuDirectPreSkinned,
                EAdvancedVisibilityRasterOrigin.Late,
                masked: true,
                frontFace: false,
                velocityValid: true,
                viewIndex: 7u,
                AdvancedVisibilityBufferContract.PayloadVersion,
                selectionValid: true,
                out AdvancedVisibilityMetadataWord word,
                out EAdvancedVisibilityPayloadOverflow overflow)
            .ShouldBeTrue();
        overflow.ShouldBe(EAdvancedVisibilityPayloadOverflow.None);
        word.Decode().ShouldBe(new AdvancedVisibilityDecodedMetadata(
            EAdvancedGeometryProducer.CpuDirectPreSkinned,
            EAdvancedVisibilityRasterOrigin.Late,
            Masked: true,
            FrontFace: false,
            VelocityValid: true,
            ViewIndex: 7u,
            PayloadVersion: 1u,
            SelectionValid: true));

        AdvancedVisibilityPrimitiveIdentity.TryEncodeIndexed(
                1234u,
                out uint indexed,
                out overflow)
            .ShouldBeTrue();
        AdvancedVisibilityPrimitiveIdentity.TryEncodeMeshlet(
                0x123456u,
                0x7Fu,
                out uint meshlet,
                out overflow)
            .ShouldBeTrue();
        AdvancedVisibilityPrimitiveIdentity.Decode(
                indexed,
                EAdvancedGeometryProducer.IndirectIndexed)
            .PrimitiveIndex.ShouldBe(1234u);
        AdvancedVisibilityDecodedPrimitive decodedMeshlet =
            AdvancedVisibilityPrimitiveIdentity.Decode(
                meshlet,
                EAdvancedGeometryProducer.StaticMeshlet);
        decodedMeshlet.MeshletOrClusterIndex.ShouldBe(0x123456u);
        decodedMeshlet.LocalPrimitiveIndex.ShouldBe(0x7Fu);
        AdvancedVisibilityPrimitiveIdentity.TryEncodeMeshlet(
                AdvancedVisibilityPrimitiveIdentity.MaximumMeshletIndex + 1u,
                0u,
                out _,
                out overflow)
            .ShouldBeFalse();
        overflow.ShouldBe(
            EAdvancedVisibilityPayloadOverflow.PrimitiveIndex);

        AdvancedVisibilityMetadataWord.TryCreate(
                (EAdvancedGeometryProducer)7u,
                EAdvancedVisibilityRasterOrigin.Early,
                false, true, true, 0u, 1u, false,
                out _,
                out overflow)
            .ShouldBeFalse();
        overflow.ShouldBe(
            EAdvancedVisibilityPayloadOverflow.Producer);
        AdvancedVisibilityMetadataWord.TryCreate(
                EAdvancedGeometryProducer.IndirectIndexed,
                (EAdvancedVisibilityRasterOrigin)2u,
                false, true, true, 0u, 1u, false,
                out _,
                out overflow)
            .ShouldBeFalse();
        overflow.ShouldBe(
            EAdvancedVisibilityPayloadOverflow.RasterOrigin);
    }

    [Test]
    public void Resolver_SelectsAllFiveProducersFromCanonicalSubmissionPolicy()
    {
        AdvancedVisibilityPayload staticPayload =
            CreatePayload(skinned: false, meshlets: true);
        AdvancedVisibilityPayload skinnedPayload =
            CreatePayload(skinned: true, meshlets: true);

        AdvancedVisibilityProducerResolver.Resolve(
                EMeshSubmissionStrategy.CpuDirect,
                staticPayload)
            .ShouldBe(
                EAdvancedGeometryProducer.CpuDirectStaticIndexed);
        AdvancedVisibilityProducerResolver.Resolve(
                EMeshSubmissionStrategy.CpuDirect,
                skinnedPayload)
            .ShouldBe(
                EAdvancedGeometryProducer.CpuDirectPreSkinned);
        AdvancedVisibilityProducerResolver.Resolve(
                EMeshSubmissionStrategy.GpuIndirectZeroReadback,
                staticPayload)
            .ShouldBe(EAdvancedGeometryProducer.IndirectIndexed);
        AdvancedVisibilityProducerResolver.Resolve(
                EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                staticPayload)
            .ShouldBe(EAdvancedGeometryProducer.StaticMeshlet);
        AdvancedVisibilityProducerResolver.Resolve(
                EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                skinnedPayload)
            .ShouldBe(EAdvancedGeometryProducer.SkinnedMeshlet);
    }

    [Test]
    public void EveryCompatibleProducer_EmitsTheSameLogicalSurfaceIdentity()
    {
        AdvancedVisibilityPrimitiveReference primitive = new(
            CanonicalPrimitiveIndex: 42u,
            MeshletOrClusterIndex: 7u,
            LocalPrimitiveIndex: 3u);
        const uint selection = 9001u;
        AdvancedVisibilityPayload staticPayload =
            CreatePayload(skinned: false, meshlets: true);
        AdvancedVisibilityPayload skinnedPayload =
            CreatePayload(skinned: true, meshlets: true);
        EAdvancedGeometryProducer[] producers =
        [
            EAdvancedGeometryProducer.CpuDirectStaticIndexed,
            EAdvancedGeometryProducer.CpuDirectPreSkinned,
            EAdvancedGeometryProducer.IndirectIndexed,
            EAdvancedGeometryProducer.StaticMeshlet,
            EAdvancedGeometryProducer.SkinnedMeshlet,
        ];

        (uint DrawTableIndex, uint CanonicalPrimitiveIndex, uint SelectionId, uint ViewIndex)? expected = null;
        foreach (EAdvancedGeometryProducer producer in producers)
        {
            AdvancedVisibilityPayload payload = producer is
                EAdvancedGeometryProducer.CpuDirectPreSkinned or
                EAdvancedGeometryProducer.SkinnedMeshlet
                    ? skinnedPayload
                    : staticPayload;
            AdvancedVisibilityProducerEncoder.TryEncode(
                    payload,
                    primitive,
                    producer,
                    EAdvancedVisibilityRasterOrigin.Early,
                    viewIndex: 1u,
                    selection,
                    frontFace: true,
                    velocityValid: true,
                    out AdvancedVisibilityEncodedSurface encoded,
                    out EAdvancedVisibilityPayloadOverflow overflow)
                .ShouldBeTrue(producer.ToString());
            overflow.ShouldBe(
                EAdvancedVisibilityPayloadOverflow.None);
            AdvancedVisibilityLogicalSurface logical =
                encoded.DecodeLogical();
            logical.Producer.ShouldBe(producer);
            primitive.TryResolve(
                    logical.Primitive,
                    out uint canonicalPrimitive)
                .ShouldBeTrue();
            var canonicalSurface = (
                logical.DrawTableIndex,
                canonicalPrimitive,
                logical.SelectionId,
                logical.ViewIndex);
            if (expected.HasValue)
                canonicalSurface.ShouldBe(expected.Value);
            else
                expected = canonicalSurface;
        }
    }

    [Test]
    public void Decoder_ResolvesMaterialTransformsAndEditorIdentityFromTables()
    {
        AdvancedVisibilityPayload payload =
            CreatePayload(skinned: false, meshlets: false);
        AdvancedVisibilityProducerEncoder.TryEncode(
                payload,
                primitive: new AdvancedVisibilityPrimitiveReference(12u, 0u, 0u),
                EAdvancedGeometryProducer.IndirectIndexed,
                EAdvancedVisibilityRasterOrigin.Late,
                viewIndex: 0u,
                selectionId: 77u,
                frontFace: true,
                velocityValid: false,
                out AdvancedVisibilityEncodedSurface encoded,
                out _)
            .ShouldBeTrue();

        AdvancedDrawRecord[] draws =
        [
            new()
            {
                Instance = new AdvancedGpuHandle(2u, 3u),
                Geometry = payload.Geometry,
                Material = payload.Material,
                EditorIdentity = new AdvancedGpuHandle(6u, 7u),
                CurrentTransform = new AdvancedGpuHandle(8u, 9u),
                PreviousTransform = new AdvancedGpuHandle(10u, 11u),
                PrimitiveSection = payload.PrimitiveSection,
            },
        ];
        AdvancedMaterialRecord[] materials =
        [
            new()
            {
                StableRowId = payload.Material.Index,
                Generation = payload.Material.Generation,
                ShadingKernelId = 17u,
            },
        ];

        AdvancedVisibilityDecoder.TryResolve(
                encoded,
                draws,
                materials,
                out AdvancedVisibilityResolvedSurface resolved)
            .ShouldBeTrue();
        resolved.Instance.ShouldBe(draws[0].Instance);
        resolved.Geometry.ShouldBe(payload.Geometry);
        resolved.Material.ShouldBe(payload.Material);
        resolved.CurrentTransform.ShouldBe(draws[0].CurrentTransform);
        resolved.PreviousTransform.ShouldBe(draws[0].PreviousTransform);
        resolved.EditorIdentity.ShouldBe(draws[0].EditorIdentity);
        resolved.ShadingKernelId.ShouldBe(17u);
    }

    [Test]
    public void Sequence_IsEarlyThenOneHzbThenLateAndPreservesEarlyTargets()
    {
        IReadOnlyList<AdvancedVisibilitySequenceOperationDescriptor> sequence =
            AdvancedVisibilitySequenceContract.Ordered;
        sequence.Select(static item => item.Operation).ShouldBe(
        [
            EAdvancedVisibilitySequenceOperation.ResetCounters,
            EAdvancedVisibilitySequenceOperation.ClearTargets,
            EAdvancedVisibilitySequenceOperation.PrepareEarlyVisibility,
            EAdvancedVisibilitySequenceOperation.ResetEarlyArgumentCounts,
            EAdvancedVisibilitySequenceOperation.BuildEarlyArguments,
            EAdvancedVisibilitySequenceOperation.RasterEarlyVisibility,
            EAdvancedVisibilitySequenceOperation.BuildCurrentDepthPyramid,
            EAdvancedVisibilitySequenceOperation.PrepareLateVisibility,
            EAdvancedVisibilitySequenceOperation.ResetLateArgumentCounts,
            EAdvancedVisibilitySequenceOperation.BuildLateArguments,
            EAdvancedVisibilitySequenceOperation.RasterLateVisibility,
            EAdvancedVisibilitySequenceOperation.ValidateFinalTargets,
            EAdvancedVisibilitySequenceOperation.PublishFinalTargets,
        ]);
        sequence.Count(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.BuildCurrentDepthPyramid)
            .ShouldBe(1);
        sequence.Single(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.RasterEarlyVisibility)
            .BoundaryBefore.ShouldBe(
                EAdvancedVisibilitySynchronizationBoundary.PreparationToEarlyRaster);
        sequence.Single(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.RasterEarlyVisibility)
            .RasterOrigin.ShouldBe(
                EAdvancedVisibilityRasterOrigin.Early);
        sequence.Single(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.RasterLateVisibility)
            .PreservesExistingVisibility.ShouldBeTrue();
        sequence.Single(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.PrepareLateVisibility)
            .BoundaryBefore.ShouldBe(
                EAdvancedVisibilitySynchronizationBoundary.DepthPyramidToLatePreparation);
        sequence.Single(static item =>
                item.Operation ==
                EAdvancedVisibilitySequenceOperation.RasterLateVisibility)
            .BoundaryBefore.ShouldBe(
                EAdvancedVisibilitySynchronizationBoundary.LatePreparationToLateRaster);
        AdvancedVisibilitySynchronizationContract.Ordered.Count.ShouldBe(5);
    }

    [Test]
    public void ShaderVariants_AreIndependentOfMaterialInstanceAndRejectUnsupportedDepth()
    {
        AdvancedVisibilityShaderCacheKey first =
            AdvancedVisibilityShaderCacheKey.Create(
                vertexLayoutId: 123UL,
                EAdvancedMaterialCoverageMode.Masked,
                EAdvancedDeformationExecutionMode.AggregateCompute,
                EAdvancedShaderViewMode.StereoArray,
                EAdvancedGeometryProducer.SkinnedMeshlet,
                RuntimeGraphicsApiKind.Vulkan);
        AdvancedVisibilityShaderCacheKey second = first with
        {
            DisplacementMode =
                EAdvancedVisibilityDisplacementMode.VertexDepthAffecting,
        };

        first.PayloadVersion.ShouldBe(
            AdvancedVisibilityBufferContract.PayloadVersion);
        first.ShouldNotBe(second);
        AdvancedVisibilityShaderVariantContract.IsSupported(
                EAdvancedMaterialCoverageMode.Masked,
                EAdvancedVisibilityDisplacementMode.None)
            .ShouldBeTrue();
        AdvancedVisibilityShaderVariantContract.IsSupported(
                EAdvancedMaterialCoverageMode.Transparent,
                EAdvancedVisibilityDisplacementMode.None)
            .ShouldBeFalse();
        AdvancedVisibilityShaderVariantContract.IsSupported(
                EAdvancedMaterialCoverageMode.Opaque,
                EAdvancedVisibilityDisplacementMode.UnsupportedFragmentDepth)
            .ShouldBeFalse();
        AdvancedVisibilityShaderVariantContract.IsSupported(
                EAdvancedMaterialCoverageMode.Opaque,
                EAdvancedVisibilityDisplacementMode.VertexDepthAffecting)
            .ShouldBeFalse();
        AdvancedVisibilityShaderVariantContract.IsSupported(
                EAdvancedMaterialCoverageMode.Opaque,
                EAdvancedVisibilityDisplacementMode.TessellatedDepthAffecting)
            .ShouldBeFalse();
        AdvancedVisibilityShaderVariantContract.ChangesRasterPosition(
                EAdvancedVisibilityDisplacementMode.VertexDepthAffecting)
            .ShouldBeTrue();
    }

    [Test]
    public void MotionInvalidation_CoversEveryRequiredHistoryEvent()
    {
        AdvancedVisibilityMotionContract.Resolve(
                newSurface: false,
                teleported: false,
                topologyChanged: false,
                vertexCountChanged: false,
                historyReset: false,
                arenaOverflow: false,
                frameGap: false)
            .ShouldBe(EAdvancedVelocityValidityReason.Valid);
        AdvancedVisibilityMotionContract.Resolve(
                false, true, false, false, false, false, false)
            .ShouldBe(EAdvancedVelocityValidityReason.Teleported);
        AdvancedVisibilityMotionContract.Resolve(
                false, false, true, false, false, false, false)
            .ShouldBe(EAdvancedVelocityValidityReason.TopologyChanged);
        AdvancedVisibilityMotionContract.Resolve(
                false, false, false, false, true, false, false)
            .ShouldBe(EAdvancedVelocityValidityReason.HistoryReset);
    }

    [Test]
    public void ResourceLayout_DeclaresNamedStereoTargetsHistoryAndFrameSlots()
    {
        AdvancedRenderPipelineCapabilityResult capabilities =
            AdvancedRenderPipelineCapabilityResolver.Resolve(
                AdvancedRenderPipelineCapabilityTests.SupportedCapabilities,
                stereo: true);
        AdvancedRenderPipeline pipeline = new(
            stereo: true,
            capabilities);
        RenderPipelineResourceProfile profile = new(
            DisplayWidth: 1920u,
            DisplayHeight: 1080u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: true,
            FeatureMask: (ulong)(
                AdvancedVisibilityResourceFeature.Core |
                AdvancedVisibilityResourceFeature.DebugOutput |
                AdvancedVisibilityResourceFeature.GpuValidation),
            ViewCount: 2u);
        RenderPipelineResourceLayout layout =
            pipeline.BuildResourceLayout(profile);

        TextureSpec identity = layout.ResourcesByName[
                AdvancedVisibilityResourceNames.Identity]
            .ShouldBeOfType<TextureSpec>();
        identity.InternalFormat.ShouldBe(EPixelInternalFormat.RG32ui);
        identity.PixelFormat.ShouldBe(EPixelFormat.RgInteger);
        identity.Layers.ShouldBe(2u);
        identity.Samples.ShouldBe(1u);
        identity.RequiresStorageUsage.ShouldBeTrue();
        (identity.Usage & RenderPipelineResourceUsage.TransferSource)
            .ShouldBe(RenderPipelineResourceUsage.TransferSource);

        TextureSpec history = layout.ResourcesByName[
                AdvancedVisibilityResourceNames.PreviousDepthPyramid]
            .ShouldBeOfType<TextureSpec>();
        history.HistoryPolicy.ShouldBe(
            RenderResourceHistoryPolicy.PreserveWhenCompatible);
        history.MipPolicy.MipLevelCount.ShouldBeGreaterThan(1u);

        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.EarlyArguments(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.LateArguments(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.EarlyMeshTaskArguments(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.LateMeshTaskArguments(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.EarlyMeshPayloads(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.LateMeshPayloads(slot));
            layout.ResourcesByName.ShouldContainKey(
                AdvancedVisibilityResourceNames.Counters(slot));
        }
        layout.ResourcesByName.ShouldContainKey(
            AdvancedVisibilityResourceNames.DebugOutput);
    }

    [Test]
    public void ShaderSources_UseCanonicalTablesAndOnlyMaskedCoverageSamplesMaterial()
    {
        string interfaceSource = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityInterface.glslinc");
        string vertex = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.vert");
        string opaque = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.frag");
        string masked = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRasterMasked.frag");
        string mesh = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRaster.mesh");
        string indirect = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Preparation/BuildVisibilityIndirect.comp");

        interfaceSource.ShouldContain(
            "#include \"Advanced/Access/AdvancedAccess.glslinc\"");
        interfaceSource.ShouldContain("XR_ADV_ResolveHandle");
        interfaceSource.ShouldContain(
            "XR_ADV_VISIBILITY_PAYLOAD_VERSION");
        interfaceSource.ShouldContain(
            "VisibilityTextureLookupSegment");
        interfaceSource.ShouldNotContain(
            "logicalReference.handle.index - 1u");
        interfaceSource.ShouldContain(
            "XR_ADV_IsSupportedVisibilityProducer");
        vertex.ShouldContain("XR_ADV_LoadDraw");
        vertex.ShouldContain("XR_ADV_LoadInstance");
        vertex.ShouldContain("XR_ADV_LoadGeometry");
        vertex.ShouldContain("XR_ADV_LoadTransform");
        opaque.ShouldNotContain("XR_ADV_SampleTexture2D");
        opaque.ShouldContain("VisibilityOrigin > 1u");
        opaque.ShouldNotContain("roughness", Case.Insensitive);
        opaque.ShouldNotContain("lighting", Case.Insensitive);
        masked.ShouldContain("XR_ADV_LoadMaterialTextureBinding");
        masked.ShouldContain("XR_ADV_SampleTexture2D");
        masked.ShouldContain("ALPHA_CUTOFF_WORD");
        masked.ShouldContain("VisibilityOrigin > 1u");
        mesh.ShouldContain("XR_ADV_VisibilityMeshlets.records");
        mesh.ShouldContain("gl_PrimitiveTriangleIndicesEXT");
        mesh.ShouldContain("gl_MeshPrimitivesEXT[primitive].gl_PrimitiveID");
        mesh.ShouldNotContain("vec2(-1.0, -1.0)");
        indirect.ShouldContain("PRODUCER_CPU_DIRECT_STATIC_INDEXED");
        indirect.ShouldContain("PRODUCER_CPU_DIRECT_PRE_SKINNED");
        indirect.ShouldContain("DrawMeshTasksIndirect");
        indirect.ShouldContain("MeshPayloadRows[outputIndex]");
    }

    private static AdvancedVisibilityPayload CreatePayload(
        bool skinned,
        bool meshlets)
        => new(
            Draw: new AdvancedGpuHandle(1u, 1u),
            Geometry: new AdvancedGpuHandle(1u, 2u),
            Material: new AdvancedGpuHandle(1u, 3u),
            GeometryOffsets: new AdvancedSceneGeometryOffsets(
                VertexOffset: 100u,
                PreviousVertexOffset: 200u,
                IndexOffset: 300u,
                WeightOffset: 400u,
                PaletteOffset: 500u,
                MeshletOffset: 0u,
                MeshletCount: meshlets ? 8u : 0u),
            PrimitiveSection: 0u,
            InstanceCount: 1u,
            FirstIndex: 0u,
            IndexCount: 300u,
            VertexCount: 100u,
            RasterStateClass: 1u,
            Coverage: EAdvancedMaterialCoverageMode.Opaque,
            CullMode: 1u,
            PrimitiveTopology: 4u,
            Skinned: skinned,
            MeshletsResident: meshlets,
            ForceCpuDiagnostic: false);
}
