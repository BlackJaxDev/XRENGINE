using System.Numerics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedAttributeReconstructionContractTests
{
    [Test]
    public void Contract_IsVersionedSelectiveAndKeepsDiagnosticsSlotLocal()
    {
        AdvancedSurfaceContract.ContractVersion.ShouldBe(1u);
        Unsafe.SizeOf<AdvancedReconstructionGpuCounters>().ShouldBe(64);
        AdvancedReconstructionShaderBindings.StaticVertices.ShouldBe(37u);
        AdvancedReconstructionShaderBindings.Counters.ShouldBe(41u);
        ((uint)EAdvancedMaterialRequiredAttributeMask.Custom0)
            .ShouldBe(1u << 8);
        ((uint)EAdvancedMaterialRequiredAttributeMask.AnalyticalDerivatives)
            .ShouldBe(1u << 11);
        ((uint)EAdvancedMaterialRequiredAttributeMask.Color1)
            .ShouldBe(1u << 12);

        IReadOnlyList<
            AdvancedReconstructionSynchronizationBoundaryDescriptor> boundaries =
            AdvancedReconstructionSynchronizationContract.Ordered;
        boundaries.Count.ShouldBe(2);
        boundaries[0].Boundary.ShouldBe(
            EAdvancedReconstructionSynchronizationBoundary
                .FinalVisibilityToReconstruction);
        (boundaries[0].OpenGlBarrierMask &
         EMemoryBarrierMask.TextureFetch)
            .ShouldBe(EMemoryBarrierMask.TextureFetch);
        (boundaries[1].ConsumerState.AccessMask &
         RenderGraphAccessMask.TransferRead)
            .ShouldBe(RenderGraphAccessMask.TransferRead);
    }

    [Test]
    public void ResourceLayout_AllocatesOnlyCountersUntilDiagnosticsAreRequested()
    {
        AdvancedRenderPipelineCapabilityResult capabilities =
            AdvancedRenderPipelineCapabilityResolver.Resolve(
                AdvancedRenderPipelineCapabilityTests.SupportedCapabilities,
                stereo: true);
        AdvancedRenderPipeline pipeline = new(
            stereo: true,
            capabilities);
        RenderPipelineResourceLayout baseline =
            pipeline.BuildResourceLayout(CreateProfile(
                stereo: true,
                featureMask:
                    (ulong)AdvancedVisibilityResourceFeature.Core |
                    (ulong)AdvancedReconstructionResourceFeature.Core));

        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            BufferSpec counters = baseline.ResourcesByName[
                    AdvancedReconstructionResourceNames.Counters(slot)]
                .ShouldBeOfType<BufferSpec>();
            counters.ElementStride.ShouldBe(64u);
            counters.ElementCount.ShouldBe(
                RenderFrameViewSet.MaxViewCount + 1u);
        }
        baseline.ResourcesByName.ShouldNotContainKey(
            AdvancedReconstructionResourceNames.DebugOutput);
        baseline.ResourcesByName.ShouldNotContainKey(
            AdvancedReconstructionResourceNames.DerivativeError);
        baseline.ResourcesByName.Keys.ShouldNotContain(
            static name => name.Contains(
                "GBuffer",
                StringComparison.OrdinalIgnoreCase));

        RenderPipelineResourceLayout diagnostics =
            pipeline.BuildResourceLayout(CreateProfile(
                stereo: true,
                featureMask: ulong.MaxValue));
        TextureSpec debug = diagnostics.ResourcesByName[
                AdvancedReconstructionResourceNames.DebugOutput]
            .ShouldBeOfType<TextureSpec>();
        debug.InternalFormat.ShouldBe(EPixelInternalFormat.Rgba16f);
        debug.Layers.ShouldBe(2u);
        debug.RequiresStorageUsage.ShouldBeTrue();
        diagnostics.ResourcesByName.ShouldContainKey(
            AdvancedReconstructionResourceNames.DerivativeError);
        diagnostics.ResourcesByName.ShouldContainKey(
            AdvancedReconstructionResourceNames.SelectedMip);
        diagnostics.ResourcesByName.ShouldContainKey(
            AdvancedReconstructionResourceNames.ReferenceOutput);
    }

    [Test]
    public void ResourceFeatureMask_TracksEveryOptionalReconstructionAllocation()
    {
        AdvancedRenderPipeline pipeline = new()
        {
            ReconstructionDebugView =
                EAdvancedReconstructionDebugView.ShadingNormal,
            EnableReconstructionDerivativeDiagnostics = true,
            EnableReconstructionGpuValidation = true,
            EnableReconstructionReferenceOutput = true,
        };
        ulong mask = pipeline.BuildResourceFeatureMaskForGenerationKey(
            new XRRenderPipelineInstance(),
            viewport: null);
        AdvancedReconstructionResourceFeature reconstruction =
            (AdvancedReconstructionResourceFeature)mask;
        (reconstruction & AdvancedReconstructionResourceFeature.Core)
            .ShouldBe(AdvancedReconstructionResourceFeature.Core);
        (reconstruction &
         AdvancedReconstructionResourceFeature.DebugOutput)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.DebugOutput);
        (reconstruction &
         AdvancedReconstructionResourceFeature.DerivativeDiagnostics)
            .ShouldBe(
                AdvancedReconstructionResourceFeature
                    .DerivativeDiagnostics);
        (reconstruction &
         AdvancedReconstructionResourceFeature.GpuValidation)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.GpuValidation);
        (reconstruction &
         AdvancedReconstructionResourceFeature.ReferenceOutput)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.ReferenceOutput);

        AdvancedRenderPipeline selectedMipView = new()
        {
            ReconstructionDebugView =
                EAdvancedReconstructionDebugView.SelectedMip,
        };
        AdvancedReconstructionResourceFeature selectedMipFeatures =
            (AdvancedReconstructionResourceFeature)
                selectedMipView.BuildResourceFeatureMaskForGenerationKey(
                    new XRRenderPipelineInstance(),
                    viewport: null);
        (selectedMipFeatures &
         AdvancedReconstructionResourceFeature.DerivativeDiagnostics)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.DerivativeDiagnostics);

        AdvancedRenderPipeline derivativeOnly = new()
        {
            EnableReconstructionDerivativeDiagnostics = true,
        };
        AdvancedReconstructionResourceFeature derivativeOnlyFeatures =
            (AdvancedReconstructionResourceFeature)
                derivativeOnly.BuildResourceFeatureMaskForGenerationKey(
                    new XRRenderPipelineInstance(),
                    viewport: null);
        (derivativeOnlyFeatures &
         AdvancedReconstructionResourceFeature.DerivativeDiagnostics)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.DerivativeDiagnostics);
        (derivativeOnlyFeatures &
         AdvancedReconstructionResourceFeature.DebugOutput)
            .ShouldBe(
                AdvancedReconstructionResourceFeature.DebugOutput);
    }

    [Test]
    public void PerspectiveInterpolation_MatchesFiniteDifferencesAndRejectsDegenerates()
    {
        Vector4 clip0 = new(-0.8f, -0.7f, 0.2f, 1.0f);
        Vector4 clip1 = new(1.2f, -0.8f, 0.4f, 1.5f);
        Vector4 clip2 = new(-0.4f, 1.4f, 0.6f, 2.0f);
        Vector2 origin = new(11.0f, 7.0f);
        Vector2 size = new(1600.0f, 900.0f);
        Vector2 pixel = new(620.25f, 410.75f);

        AdvancedPerspectiveInterpolation.TryReconstruct(
                pixel,
                clip0,
                clip1,
                clip2,
                origin,
                size,
                out AdvancedBarycentricDerivatives result)
            .ShouldBeTrue();
        (result.Weights.X + result.Weights.Y + result.Weights.Z)
            .ShouldBe(1.0f, 1.0e-5);
        (result.Dx.X + result.Dx.Y + result.Dx.Z)
            .ShouldBe(0.0f, 1.0e-6);
        (result.Dy.X + result.Dy.Y + result.Dy.Z)
            .ShouldBe(0.0f, 1.0e-6);

        const float epsilon = 0.01f;
        AdvancedPerspectiveInterpolation.TryReconstruct(
            pixel + new Vector2(epsilon, 0.0f),
            clip0,
            clip1,
            clip2,
            origin,
            size,
            out AdvancedBarycentricDerivatives xNeighbor).ShouldBeTrue();
        AdvancedPerspectiveInterpolation.TryReconstruct(
            pixel + new Vector2(0.0f, epsilon),
            clip0,
            clip1,
            clip2,
            origin,
            size,
            out AdvancedBarycentricDerivatives yNeighbor).ShouldBeTrue();
        Vector3.Distance(
                (xNeighbor.Weights - result.Weights) / epsilon,
                result.Dx)
            .ShouldBeLessThan(2.0e-5f);
        Vector3.Distance(
                (yNeighbor.Weights - result.Weights) / epsilon,
                result.Dy)
            .ShouldBeLessThan(2.0e-5f);

        AdvancedPerspectiveInterpolation.Interpolate(
                Vector2.One,
                new Vector2(20.0f),
                new Vector2(30.0f),
                result.Weights,
                flatQualified: true)
            .ShouldBe(Vector2.One);
        AdvancedPerspectiveInterpolation.TryReconstruct(
                pixel,
                clip0,
                clip0,
                clip0,
                origin,
                size,
                out _)
            .ShouldBeFalse();
    }

    [Test]
    public void TangentFrame_HandlesNonUniformAndMirroredTransforms()
    {
        Matrix4x4 world =
            Matrix4x4.CreateScale(-2.0f, 3.0f, 0.5f) *
            Matrix4x4.CreateTranslation(3.0f, -2.0f, 5.0f);
        AdvancedReconstructionTangentSpace.TryCreate(
                world,
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitX,
                localHandedness: 1.0f,
                out AdvancedReconstructionTangentFrame frame)
            .ShouldBeTrue();

        frame.MirroredTransform.ShouldBeTrue();
        frame.Handedness.ShouldBe(-1.0f);
        Vector3.Distance(
                frame.GeometricNormal,
                -Vector3.UnitZ)
            .ShouldBeLessThan(1.0e-5f);
        Vector3.Distance(frame.ShadingNormal, Vector3.UnitZ)
            .ShouldBeLessThan(1.0e-5f);
        Vector3.Dot(frame.ShadingNormal, frame.Tangent)
            .ShouldBe(0.0f, 1.0e-5);
        Vector3.Dot(frame.ShadingNormal, frame.Bitangent)
            .ShouldBe(0.0f, 1.0e-5);
    }

    [Test]
    public void TemporalContract_UsesUnjitteredNdcAndReactiveInvalidation()
    {
        AdvancedReconstructionMotion leftEye =
            AdvancedReconstructionTemporalContract.Resolve(
                new Vector4(0.5f, 0.25f, 0.0f, 1.0f),
                new Vector4(0.0f, 0.25f, 0.0f, 1.0f),
                EAdvancedVelocityValidityReason.Valid,
                maskedEdge: false);
        AdvancedReconstructionMotion rightEye =
            AdvancedReconstructionTemporalContract.Resolve(
                new Vector4(-0.25f, 0.0f, 0.0f, 1.0f),
                new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                EAdvancedVelocityValidityReason.Valid,
                maskedEdge: true);
        leftEye.NdcMotion.ShouldBe(new Vector2(0.5f, 0.0f));
        leftEye.IsValid.ShouldBeTrue();
        rightEye.NdcMotion.ShouldBe(new Vector2(-0.25f, 0.0f));
        rightEye.IsReactive.ShouldBeTrue();

        AdvancedReconstructionMotion reset =
            AdvancedReconstructionTemporalContract.Resolve(
                Vector4.One,
                Vector4.One,
                EAdvancedVelocityValidityReason.NewlyVisible,
                maskedEdge: false);
        reset.IsValid.ShouldBeFalse();
        reset.IsReactive.ShouldBeTrue();
        reset.NdcMotion.ShouldBe(Vector2.Zero);
    }

    [Test]
    public void Decoder_RejectsInvalidMissingAndStaleDrawsDeterministically()
    {
        AdvancedSharedGpuSceneDatabase database =
            new(CreateDatabaseCapacity());
        AdvancedReconstructionDecoder.TryResolve(
                AdvancedVisibilityEncodedSurface.Invalid,
                database,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out EAdvancedReconstructionInvalidReason invalid)
            .ShouldBeFalse();
        invalid.ShouldBe(
            EAdvancedReconstructionInvalidReason.BackgroundOrInvalidPayload);

        AdvancedGpuHandle fakeMaterial = new(1u, 1u);
        database.Scene.Draws.TryAdd(
                new AdvancedDrawRecord
                {
                    Material = fakeMaterial,
                    Instance = new AdvancedGpuHandle(1u, 1u),
                    Geometry = new AdvancedGpuHandle(1u, 1u),
                    CurrentTransform = new AdvancedGpuHandle(1u, 1u),
                    PreviousTransform = new AdvancedGpuHandle(1u, 1u),
                    RenderState = new AdvancedGpuHandle(1u, 1u),
                    EditorIdentity = new AdvancedGpuHandle(1u, 1u),
                },
                out AdvancedGpuHandle drawHandle)
            .ShouldBeTrue();
        database.PublishHandleLookups().ShouldBeTrue();
        AdvancedVisibilityPayload payload = new(
            drawHandle,
            new AdvancedGpuHandle(1u, 1u),
            fakeMaterial,
            default,
            PrimitiveSection: 0u,
            InstanceCount: 1u,
            FirstIndex: 0u,
            IndexCount: 3u,
            VertexCount: 3u,
            RasterStateClass: 0u,
            Coverage: EAdvancedMaterialCoverageMode.Opaque,
            CullMode: 0u,
            PrimitiveTopology: 4u,
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

        AdvancedReconstructionDecoder.TryResolve(
                encoded,
                database,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out invalid)
            .ShouldBeFalse();
        invalid.ShouldBe(
            EAdvancedReconstructionInvalidReason
                .StaleDependencyGeneration);

        database.Scene.Draws.TryRemoveImmediatelyBeforePublication(drawHandle).ShouldBeTrue();
        database.PublishHandleLookups().ShouldBeTrue();
        AdvancedReconstructionDecoder.TryResolve(
                encoded,
                database,
                ReadOnlySpan<AdvancedViewRecord>.Empty,
                out _,
                out invalid)
            .ShouldBeFalse();
        invalid.ShouldBe(
            EAdvancedReconstructionInvalidReason.DrawNotResident);
    }

    [Test]
    public void ShaderSources_ReconstructOnDemandWithoutAClassicGBuffer()
    {
        string surface = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Reconstruction/AdvancedSurface.glslinc");
        string reconstruction = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Reconstruction/ReconstructSurface.glslinc");
        string interfaceSource = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Reconstruction/ReconstructionInterface.glslinc");
        string reference = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Reconstruction/ReconstructionReference.comp");
        string debug = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Reconstruction/ReconstructionDebug.comp");
        string textureAccess = SourceContractWorkspace.ReadFile(
            "Build/CommonAssets/Shaders/Advanced/Access/AdvancedTextureAccess.glslinc");

        surface.ShouldContain("XR_ADV_REQUIRED_ATTRIBUTE_MASK");
        surface.ShouldContain("XR_ADV_ATTRIBUTE_FLAT");
        reconstruction.ShouldContain(
            "XR_ADV_TryPerspectiveBarycentrics");
        reconstruction.ShouldContain(
            "inverseViewProjectionJittered");
        reconstruction.ShouldContain(
            "previousViewProjectionUnjittered");
        reconstruction.ShouldContain(
            "transpose(inverse(world3))");
        reconstruction.ShouldContain(
            "XR_ADV_SURFACE_CONSERVATIVE_MIP");
        reconstruction.ShouldContain(
            "geometry.source == XR_ADV_GEOMETRY_SOURCE_PRESKINNED");
        reconstruction.ShouldContain(
            "geometry.fallbackGeometry");
        reconstruction.ShouldContain(
            "bool requiresTangent");
        reconstruction.ShouldContain(
            "bool derivativesRequested");
        reconstruction.ShouldContain(
            "XR_ADV_SampleTexture2DGrad");
        reconstruction.ShouldContain(
            "XR_ADV_SampleTexture2DLod");
        interfaceSource.ShouldContain(
            "XR_ADV_PreSkinnedCurrentVertices");
        interfaceSource.ShouldContain(
            "XR_ADV_ReconstructionMeshlets");
        interfaceSource.ShouldContain(
            "set = XR_ADV_VISIBILITY_SET");
        reference.ShouldContain(
            "XR_ADV_RECONSTRUCTION_PASS_LAYOUT(0)");
        textureAccess.ShouldContain("textureGrad");
        textureAccess.ShouldContain("textureLod");
        reference.ShouldContain(
            "Non-production full-screen reference");
        debug.ShouldContain("DebugView == 15u");
        debug.ShouldContain("DebugView == 16u");
        (surface + reconstruction + interfaceSource)
            .ShouldNotContain("GBuffer", Case.Insensitive);
    }

    private static RenderPipelineResourceProfile CreateProfile(
        bool stereo,
        ulong featureMask)
        => new(
            DisplayWidth: 1920u,
            DisplayHeight: 1080u,
            InternalWidth: 1280u,
            InternalHeight: 720u,
            OutputHDR: true,
            AntiAliasingMode: EAntiAliasingMode.None,
            MsaaSampleCount: 1u,
            Stereo: stereo,
            FeatureMask: featureMask,
            ViewCount: stereo ? 2u : 1u);

    private static AdvancedSharedGpuSceneCapacityProfile
        CreateDatabaseCapacity()
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
