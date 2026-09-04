using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    public const string ExternalOutputResourceName = "$ExternalOutput";

    /// <summary>
    /// Captures the complete immutable resource/state profile for this pipeline.
    /// </summary>
    internal AdvancedRenderResourceProfile CaptureAdvancedResourceProfile(
        XRRenderPipelineInstance instance,
        in RenderPipelineResourceProfile targetProfile)
        => AdvancedRenderResourceProfile.CreateAttributeReconstruction(
            targetProfile,
            instance.AdvancedOutputBinding.State ==
                EAdvancedRenderPipelineOutputBindingState.Unconfigured
                    ? CapabilityResult.Capabilities
                    : instance.AdvancedOutputBinding.CapabilityResult.Capabilities);

    /// <summary>
    /// Captures every optional visibility/capture allocation before generation.
    /// </summary>
    internal override ulong BuildResourceFeatureMaskForGenerationKey(
        XRRenderPipelineInstance instance,
        XRViewport? viewport)
    {
        AdvancedVisibilityResourceFeature visibilityFeatures =
            AdvancedVisibilityResourceFeature.Core;
        if (VisibilityDebugView != EAdvancedVisibilityDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            visibilityFeatures |= AdvancedVisibilityResourceFeature.DebugOutput;
        }
        if (EnableVisibilityGpuValidation)
            visibilityFeatures |= AdvancedVisibilityResourceFeature.GpuValidation;

        AdvancedReconstructionResourceFeature reconstructionFeatures =
            AdvancedReconstructionResourceFeature.Core;
        if (ReconstructionDebugView !=
                EAdvancedReconstructionDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.DebugOutput;
        }
        bool derivativeDebugView =
            ReconstructionDebugView is
                EAdvancedReconstructionDebugView.DerivativeError or
                EAdvancedReconstructionDebugView.SelectedMip;
        if (EnableReconstructionDerivativeDiagnostics ||
            derivativeDebugView)
        {
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.DerivativeDiagnostics |
                AdvancedReconstructionResourceFeature.DebugOutput;
        }
        if (EnableReconstructionGpuValidation)
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.GpuValidation;
        if (EnableReconstructionReferenceOutput)
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.ReferenceOutput;

        AdvancedClassificationResourceFeature classificationFeatures =
            AdvancedClassificationResourceFeature.Standard;
        if (ClassificationDebugView != EAdvancedClassificationDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            classificationFeatures |= AdvancedClassificationResourceFeature.DebugOutput;
        }

        ulong shadingFeatureMask = 0UL;
        if (ShadingDebugView != EAdvancedShadingDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            shadingFeatureMask = (1UL << 40);
        }

        ulong latePassFeatureMask = 0UL;
        if (LatePassDebugView != EAdvancedLatePassDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            latePassFeatureMask = (1UL << 48);
        }

        return (ulong)visibilityFeatures | (ulong)reconstructionFeatures | ((ulong)classificationFeatures << 32) | shadingFeatureMask | latePassFeatureMask;
    }

    /// <summary>
    /// Declares the document-04/05/06/07/08 visibility, reconstruction, classification, native shading, and late-pass contracts.
    /// </summary>
    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        DeclareVisibilityBufferResources(builder);
        DeclareAttributeReconstructionResources(builder);
        DeclareClassificationResources(builder);
        DeclareNativeShadingResources(builder);
        // Thumbnail and depth/visibility captures consume the native visibility
        // outputs directly.  Do not realize the late/post graph for those views.
        if (AllowsLateTransparency)
            DeclareTransparencyAndLatePassResources(builder);
        if (AllowsLateTransparency || AllowsPostProcessing)
            DeclareLateAndPostExecutionResources(builder);

        RenderPipelineExternalTargetKind kind = builder.Profile.ExternalTargetKind;
        if (kind == RenderPipelineExternalTargetKind.None)
            return;

        ExternalRenderResourceOwnership ownership = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceOwnership.Window,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceOwnership.XrRuntime,
            _ => ExternalRenderResourceOwnership.Caller,
        };
        ExternalRenderResourceSynchronization synchronization = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceSynchronization.FrameBoundary,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceSynchronization.AcquireRelease,
            _ => ExternalRenderResourceSynchronization.CallerProvided,
        };

        builder.External(ExternalOutputResourceName)
            .Contract(
                ExternalRenderResourceKind.FrameBuffer,
                ownership,
                synchronization)
            .DebugLabel(kind.ToString())
            .Add();
    }

    private void DeclareLateAndPostExecutionResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy windowSize = RenderResourceSizePolicy.Window();
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);

        DeclareLatePostColor(builder, TransparentSceneCopyTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, TransparentAccumTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, TransparentRevealageTextureName, internalSize, layers, EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, ESizedInternalFormat.R8);
        DeclareLatePostColor(builder, BloomBlurTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, mips: 5u);
        DeclareLatePostColor(builder, AutoExposureTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), 1u, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f);
        DeclareLatePostColor(builder, PostProcessOutputTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, FinalPostProcessOutputTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, HistoryColorTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, history: true);
        DeclareLatePostColor(builder, TemporalColorInputTextureName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, TemporalExposureVarianceTextureName, internalSize, layers, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f);
        DeclareLatePostColor(builder, HistoryExposureVarianceTextureName, internalSize, layers, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f, history: true);
        DeclareLatePostColor(builder, FxaaOutputTextureName, windowSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, SmaaEdgeTextureName, windowSize, layers, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8);
        DeclareLatePostColor(builder, SmaaBlendTextureName, windowSize, layers, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8);
        DeclareLatePostColor(builder, SmaaOutputTextureName, windowSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, TsrOutputTextureName, windowSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, TsrHistoryColorTextureName, windowSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, history: true);
        builder.TextureView(DepthStencilTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0u, layers).Target(array: Stereo, multisample: false)
            .Factory(CreateAdvancedVisibilityDepthStencilAlias).Add();
        builder.TextureView(DepthViewTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.SampledTexture)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0u, layers).Target(array: Stereo, multisample: false)
            .Factory(CreateAdvancedVisibilityDepthView).Add();
        builder.TextureView(StencilViewTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.SampledTexture)
            .DepthStencilAspect(EDepthStencilFmt.Stencil)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0u, layers).Target(array: Stereo, multisample: false)
            .Factory(CreateAdvancedVisibilityStencilView).Add();
        builder.Texture(HistoryDepthStencilTextureName).Lifetime(RenderResourceLifetime.Persistent).Size(internalSize)
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Depth32fStencil8, EPixelFormat.DepthStencil, EPixelType.Float32UnsignedInt248Rev)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).Factory(CreateHistoryDepthStencilTexture)
            .Layers(layers).StereoCompatible(layers > 1u).History(RenderResourceHistoryPolicy.SeedFromCurrentFrame).Add();
        builder.TextureView(HistoryDepthViewTextureName, HistoryDepthStencilTextureName).Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.SampledTexture)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0u, layers).Target(array: Stereo, multisample: false)
            .Factory(CreateHistoryDepthViewTexture).Add();

        builder.FrameBuffer(ForwardPassFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, HDRSceneTextureName).DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(CreateForwardPassFBO).Add();
        builder.FrameBuffer(TransparentSceneCopyFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, TransparentSceneCopyTextureName)
            .Factory(CreateTransparentSceneCopyFBO).Add();
        builder.FrameBuffer(TransparentAccumulationFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, TransparentAccumTextureName).Color(1, TransparentRevealageTextureName)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(CreateTransparentAccumulationFBO).Add();
        builder.FrameBuffer(TransparentResolveFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, HDRSceneTextureName)
            .DependsOn(TransparentSceneCopyTextureName, TransparentAccumTextureName, TransparentRevealageTextureName)
            .Factory(CreateTransparentResolveFBO).Add();
        builder.QuadMaterial(SceneCopyFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(HDRSceneTextureName).Factory(CreateSceneCopyFBO).Add();
        builder.FrameBuffer(PostProcessOutputFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, PostProcessOutputTextureName)
            .Factory(CreatePostProcessOutputFBO).Add();
        builder.FrameBuffer(FinalPostProcessOutputFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, FinalPostProcessOutputTextureName)
            .Factory(CreateFinalPostProcessOutputFBO).Add();
        builder.QuadMaterial(PostProcessFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(HDRSceneTextureName, BloomBlurTextureName, DepthViewTextureName, StencilViewTextureName, AutoExposureTextureName)
            .Factory(CreatePostProcessFBO).Add();
        builder.QuadMaterial(FinalPostProcessFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(PostProcessOutputTextureName).Factory(CreateFinalPostProcessFBO).Add();
        DeclareAdvancedBloomFrameBuffers(builder, internalSize);
        DeclareAdvancedPostEffects(builder, internalSize);
        builder.FrameBuffer(HistoryCaptureFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, HistoryColorTextureName).DepthStencil(HistoryDepthStencilTextureName).Factory(CreateHistoryCaptureFBO).Add();
        builder.FrameBuffer(TemporalInputFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, TemporalColorInputTextureName).Factory(CreateTemporalInputFBO).Add();
        builder.QuadMaterial(TemporalAccumulationFBOName).Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(TemporalColorInputTextureName, HistoryColorTextureName, VelocityTextureName, DepthViewTextureName, HistoryDepthViewTextureName, HistoryExposureVarianceTextureName)
            .Factory(CreateTemporalAccumulationFBO).Add();
        builder.FrameBuffer(HistoryExposureFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, HistoryExposureVarianceTextureName).Factory(CreateHistoryExposureFBO).Add();
        DeclareLatePostDestination(builder, FxaaFBOName, FxaaOutputTextureName, windowSize, CreateFxaaFBO);
        DeclareLatePostDestination(builder, SmaaEdgeFBOName, SmaaEdgeTextureName, windowSize, CreateSmaaEdgeFBO);
        DeclareLatePostDestination(builder, SmaaBlendFBOName, SmaaBlendTextureName, windowSize, CreateSmaaBlendFBO);
        DeclareLatePostDestination(builder, SmaaFBOName, SmaaOutputTextureName, windowSize, CreateSmaaFBO);
        DeclareLatePostDestination(builder, TsrHistoryColorFBOName, TsrHistoryColorTextureName, windowSize, CreateTsrHistoryColorFBO);
        builder.FrameBuffer(TsrUpscaleFBOName).Size(windowSize).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, TsrOutputTextureName)
            .DependsOn(FinalPostProcessOutputTextureName, VelocityTextureName, DepthViewTextureName, HistoryDepthViewTextureName, TsrHistoryColorTextureName, StencilViewTextureName)
            .Factory(CreateTsrUpscaleFBO).Add();

        DeclareAdvancedExactTransparencyResources(builder, internalSize, layers);
    }

    private void DeclareAdvancedExactTransparencyResources(RenderPipelineResourceLayoutBuilder builder, RenderResourceSizePolicy internalSize, uint layers)
    {
        DeclareLatePostColor(builder, PpllHeadPointerTextureName, internalSize, layers, EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, ESizedInternalFormat.R32ui);
        DeclareLatePostColor(builder, PpllFragmentCountTextureName, internalSize, layers, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f);
        builder.Buffer(PpllNodeBufferName).Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)ComputePpllNodeCapacity(builder.Profile) * PpllNodeStrideBytes, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicCopy)
            .Elements(PpllNodeStrideBytes, ComputePpllNodeCapacity(builder.Profile)).Access(EBufferAccessPattern.ReadWrite).Factory(CreatePpllNodeBuffer).Add();
        builder.Buffer(PpllCounterBufferName).Lifetime(RenderResourceLifetime.Persistent).Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat(2u * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicCopy)
            .Elements(sizeof(uint), 2u).Access(EBufferAccessPattern.ReadWrite).Factory(CreatePpllCounterBuffer).Add();
        builder.FrameBuffer(PpllResolveFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, HDRSceneTextureName).Color(1, PpllFragmentCountTextureName)
            .DependsOn(TransparentSceneCopyTextureName, PpllHeadPointerTextureName, PpllNodeBufferName, PpllCounterBufferName).Factory(CreatePpllResolveFBO).Add();

        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            int capture = layerIndex;
            string colorName = DepthPeelColorTextureName(capture);
            string depthName = DepthPeelDepthTextureName(capture);
            DeclareLatePostColor(builder, colorName, internalSize, layers, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
            VisibilityTexture(builder, depthName, internalSize,
                RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture,
                EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float,
                ESizedInternalFormat.DepthComponent32f, EFrameBufferAttachment.DepthAttachment, storage: false)
                .Layers(layers).StereoCompatible(layers > 1u).Add();
            builder.FrameBuffer(DepthPeelLayerFboName(capture)).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
                .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
                .Color(0, colorName).Depth(depthName).Factory(() => CreateDepthPeelLayerFBO(capture)).Add();
        }

        builder.FrameBuffer(DepthPeelingResolveFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, HDRSceneTextureName)
            .DependsOn(TransparentSceneCopyTextureName, DepthPeelColorTextureName(0), DepthPeelDepthTextureName(0)).Factory(CreateDepthPeelingResolveFBO).Add();
    }

    private void DeclareAdvancedBloomFrameBuffers(RenderPipelineResourceLayoutBuilder builder, RenderResourceSizePolicy internalSize)
    {
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomMip0FBOName, internalSize, 0);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS1FBOName, internalSize, 1);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS2FBOName, internalSize, 2);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS3FBOName, internalSize, 3);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS4FBOName, internalSize, 4);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS3FBOName, internalSize, 3);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS2FBOName, internalSize, 2);
        DeclareAdvancedBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS1FBOName, internalSize, 1);
    }

    private void DeclareAdvancedBloomFrameBuffer(RenderPipelineResourceLayoutBuilder builder, string name, RenderResourceSizePolicy size, int mipLevel)
        => builder.FrameBuffer(name).Size(size).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, BloomBlurTextureName, mipLevel)
            .Factory(() => (_advancedBloomProvider ?? throw new InvalidOperationException("Advanced bloom provider was not created before resource realization."))
                .CreateDeclaredFrameBuffer(TryCurrentPipeline!, name)).Add();

    private void DeclareAdvancedPostEffects(RenderPipelineResourceLayoutBuilder builder, RenderResourceSizePolicy internalSize)
    {
        DeclareLatePostColor(builder, MotionBlurTextureName, internalSize, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, DepthOfFieldTextureName, internalSize, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostDestination(builder, MotionBlurCopyFBOName, MotionBlurTextureName, internalSize, CreateMotionBlurCopyFBO);
        DeclareLatePostDestination(builder, DepthOfFieldCopyFBOName, DepthOfFieldTextureName, internalSize, CreateDepthOfFieldCopyFBO);
        builder.QuadMaterial(MotionBlurFBOName).Lifetime(RenderResourceLifetime.Transient).DependsOn(MotionBlurTextureName, VelocityTextureName, DepthViewTextureName).Factory(CreateMotionBlurFBO).Add();
        builder.QuadMaterial(DepthOfFieldFBOName).Lifetime(RenderResourceLifetime.Transient).DependsOn(DepthOfFieldTextureName, DepthViewTextureName).Factory(CreateDepthOfFieldFBO).Add();

        DeclareAdvancedHalfEffect(builder, AtmosphereColorTextureName, AtmosphereHalfDepthTextureName, AtmosphereHalfScatterTextureName, AtmosphereHalfTemporalTextureName, AtmosphereHalfHistoryTextureName,
            AtmosphereHalfDepthQuadFBOName, AtmosphereHalfDepthFBOName, AtmosphereHalfScatterQuadFBOName, AtmosphereHalfScatterFBOName, AtmosphereReprojectQuadFBOName, AtmosphereReprojectFBOName, AtmosphereHistoryFBOName, AtmosphereUpscaleQuadFBOName, AtmosphereUpscaleFBOName,
            CreateAtmosphereHalfDepthQuadFBO, CreateAtmosphereHalfDepthFBO, CreateAtmosphereHalfScatterQuadFBO, CreateAtmosphereHalfScatterFBO, CreateAtmosphereReprojectQuadFBO, CreateAtmosphereReprojectFBO, CreateAtmosphereHistoryFBO, CreateAtmosphereUpscaleQuadFBO, CreateAtmosphereUpscaleFBO);
        DeclareAdvancedHalfEffect(builder, VolumetricFogColorTextureName, VolumetricFogHalfDepthTextureName, VolumetricFogHalfScatterTextureName, VolumetricFogHalfTemporalTextureName, VolumetricFogHalfHistoryTextureName,
            VolumetricFogHalfDepthQuadFBOName, VolumetricFogHalfDepthFBOName, VolumetricFogHalfScatterQuadFBOName, VolumetricFogHalfScatterFBOName, VolumetricFogReprojectQuadFBOName, VolumetricFogReprojectFBOName, VolumetricFogHistoryFBOName, VolumetricFogUpscaleQuadFBOName, VolumetricFogUpscaleFBOName,
            CreateVolumetricFogHalfDepthQuadFBO, CreateVolumetricFogHalfDepthFBO, CreateVolumetricFogHalfScatterQuadFBO, CreateVolumetricFogHalfScatterFBO, CreateVolumetricFogReprojectQuadFBO, CreateVolumetricFogReprojectFBO, CreateVolumetricFogHistoryFBO, CreateVolumetricFogUpscaleQuadFBO, CreateVolumetricFogUpscaleFBO);
    }

    private void DeclareAdvancedHalfEffect(RenderPipelineResourceLayoutBuilder builder, string output, string halfDepth, string halfScatter, string halfTemporal, string halfHistory,
        string depthQuad, string depthFbo, string scatterQuad, string scatterFbo, string reprojectQuad, string reprojectFbo, string historyFbo, string upscaleQuad, string upscaleFbo,
        Func<XRFrameBuffer> depthQuadFactory, Func<XRFrameBuffer> depthFboFactory, Func<XRFrameBuffer> scatterQuadFactory, Func<XRFrameBuffer> scatterFboFactory, Func<XRFrameBuffer> reprojectQuadFactory, Func<XRFrameBuffer> reprojectFboFactory, Func<XRFrameBuffer> historyFboFactory, Func<XRFrameBuffer> upscaleQuadFactory, Func<XRFrameBuffer> upscaleFboFactory)
    {
        RenderResourceSizePolicy half = RenderResourceSizePolicy.Internal(0.5f);
        RenderResourceSizePolicy full = RenderResourceSizePolicy.Internal();
        DeclareLatePostColor(builder, output, full, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, halfDepth, half, 1u, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f);
        DeclareLatePostColor(builder, halfScatter, half, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, halfTemporal, half, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f);
        DeclareLatePostColor(builder, halfHistory, half, 1u, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, history: true);
        builder.QuadMaterial(depthQuad).Lifetime(RenderResourceLifetime.Transient).DependsOn(DepthViewTextureName).Factory(depthQuadFactory).Add();
        DeclareLatePostDestination(builder, depthFbo, halfDepth, half, depthFboFactory);
        builder.QuadMaterial(scatterQuad).Lifetime(RenderResourceLifetime.Transient).DependsOn(halfDepth).Factory(scatterQuadFactory).Add();
        DeclareLatePostDestination(builder, scatterFbo, halfScatter, half, scatterFboFactory);
        builder.QuadMaterial(reprojectQuad).Lifetime(RenderResourceLifetime.Transient).DependsOn(halfScatter, halfHistory, halfDepth).Factory(reprojectQuadFactory).Add();
        DeclareLatePostDestination(builder, reprojectFbo, halfTemporal, half, reprojectFboFactory);
        DeclareLatePostDestination(builder, historyFbo, halfHistory, half, historyFboFactory);
        builder.QuadMaterial(upscaleQuad).Lifetime(RenderResourceLifetime.Transient).DependsOn(halfTemporal, halfDepth, DepthViewTextureName).Factory(upscaleQuadFactory).Add();
        DeclareLatePostDestination(builder, upscaleFbo, output, full, upscaleFboFactory);
    }

    private void DeclareLatePostColor(RenderPipelineResourceLayoutBuilder builder, string name, RenderResourceSizePolicy size, uint layers,
        EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType, ESizedInternalFormat sizedFormat, bool history = false, uint mips = 1u)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        uint width = ResolveLatePostExtent(size, profile.DisplayWidth, profile.InternalWidth, size.Width, size.ScaleX);
        uint height = ResolveLatePostExtent(size, profile.DisplayHeight, profile.InternalHeight, size.Height, size.ScaleY);
        // Factories close over the declared generation, including half-resolution
        // effects, display-resolution AA outputs, and the bloom mip chain.
        var texture = ReconstructionTexture(builder, name, size, internalFormat, pixelFormat, pixelType, sizedFormat)
            .Layers(layers).StereoCompatible(layers > 1u)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.TransferSource |
                RenderPipelineResourceUsage.TransferDestination)
            .Factory(() => CreateLatePostColor(name, width, height, layers, mips,
                internalFormat, pixelFormat, pixelType, sizedFormat));
        if (history)
            texture.History(RenderResourceHistoryPolicy.SeedFromCurrentFrame);
        if (mips > 1u)
            texture.Mips(new RenderResourceMipPolicy(0u, mips, AutoGenerateMipmaps: false, RequireImmutableStorage: true));
        texture.Add();
    }

    private static uint ResolveLatePostExtent(RenderResourceSizePolicy size,
        uint displayExtent, uint internalExtent, uint absoluteExtent, float scale)
    {
        if (size.SizeClass == RenderResourceSizeClass.AbsolutePixels)
            return Math.Max(1u, absoluteExtent);
        uint extent = Math.Max(1u, size.SizeClass == RenderResourceSizeClass.InternalResolution
            ? internalExtent : displayExtent);
        return size.RoundUpDivisor > 1u
            ? checked((extent + size.RoundUpDivisor - 1u) / size.RoundUpDivisor)
            : (uint)Math.Max(1, (int)MathF.Round(extent * scale));
    }

    private static XRTexture CreateLatePostColor(string name, uint width, uint height, uint layers,
        uint mips, EPixelInternalFormat internalFormat, EPixelFormat pixelFormat,
        EPixelType pixelType, ESizedInternalFormat sizedFormat)
    {
        XRTexture result;
        if (layers > 1u)
        {
            XRTexture2DArray array = XRTexture2DArray.CreateFrameBufferTexture(
                layers, width, height, internalFormat, pixelFormat, pixelType);
            array.OVRMultiViewParameters = new(0, layers);
            array.SmallestAllowedMipmapLevel = checked((int)mips - 1);
            array.LargestMipmapLevel = 0;
            array.MinFilter = mips > 1u ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear;
            array.MagFilter = ETexMagFilter.Linear;
            array.UWrap = array.VWrap = ETexWrapMode.ClampToEdge;
            array.SizedInternalFormat = sizedFormat;
            array.Resizable = false;
            result = array;
        }
        else
        {
            XRTexture2D mono = XRTexture2D.CreateFrameBufferTexture(
                width, height, internalFormat, pixelFormat, pixelType, EFrameBufferAttachment.ColorAttachment0);
            mono.SmallestAllowedMipmapLevel = checked((int)mips - 1);
            mono.LargestMipmapLevel = 0;
            mono.MinFilter = pixelFormat == EPixelFormat.RedInteger ? ETexMinFilter.Nearest :
                mips > 1u ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear;
            mono.MagFilter = pixelFormat == EPixelFormat.RedInteger ? ETexMagFilter.Nearest : ETexMagFilter.Linear;
            mono.UWrap = mono.VWrap = ETexWrapMode.ClampToEdge;
            mono.SizedInternalFormat = sizedFormat;
            mono.Resizable = false;
            result = mono;
        }
        result.Name = name;
        result.SamplerName = name;
        result.AutoGenerateMipmaps = false;
        result.RequiresStorageUsage = true;
        return result;
    }

    private static void DeclareLatePostDestination(RenderPipelineResourceLayoutBuilder builder, string fboName, string textureName,
        RenderResourceSizePolicy size, Func<XRFrameBuffer> factory)
        => builder.FrameBuffer(fboName).Size(size).Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment).Color(0, textureName).Factory(factory).Add();

    private XRTexture CreateAdvancedVisibilityDepthView()
        => CreateAdvancedVisibilityDepthStencilView(EDepthStencilFmt.Depth, DepthViewTextureName);

    private XRTexture CreateAdvancedVisibilityStencilView()
        => CreateAdvancedVisibilityDepthStencilView(EDepthStencilFmt.Stencil, StencilViewTextureName);

    private XRTexture CreateAdvancedVisibilityDepthStencilAlias()
    {
        if (Stereo)
            return new XRTexture2DArrayView(GetTexture<XRTexture2DArray>(AdvancedVisibilityResourceNames.DepthStencil)!, 0u, 1u, 0u, 2u, ESizedInternalFormat.Depth32fStencil8, true, false)
            { Name = DepthStencilTextureName, SamplerName = DepthStencilTextureName };

        return new XRTexture2DView(GetTexture<XRTexture2D>(AdvancedVisibilityResourceNames.DepthStencil)!, 0u, 1u, ESizedInternalFormat.Depth32fStencil8, false, false)
        { Name = DepthStencilTextureName, SamplerName = DepthStencilTextureName };
    }

    private XRTexture CreateAdvancedVisibilityDepthStencilView(EDepthStencilFmt format, string name)
    {
        if (Stereo)
        {
            return new XRTexture2DArrayView(GetTexture<XRTexture2DArray>(AdvancedVisibilityResourceNames.DepthStencil)!, 0u, 1u, 0u, 2u, ESizedInternalFormat.Depth32fStencil8, true, false)
            { DepthStencilViewFormat = format, MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest, Name = name, SamplerName = name };
        }

        return new XRTexture2DView(GetTexture<XRTexture2D>(AdvancedVisibilityResourceNames.DepthStencil)!, 0u, 1u, ESizedInternalFormat.Depth32fStencil8, false, false)
        { DepthStencilViewFormat = format, MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest, Name = name, SamplerName = name };
    }
}
