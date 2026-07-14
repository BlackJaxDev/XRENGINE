using System.Buffers.Binary;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
{
    private const ulong AoModeFieldMask = 0xFUL << 26;
    private const int AoModeFieldShift = 26;

    private const RenderPipelineResourceUsage SampledColorAttachment =
        RenderPipelineResourceUsage.SampledTexture |
        RenderPipelineResourceUsage.ColorAttachment |
        RenderPipelineResourceUsage.TransferSource;

    private const RenderPipelineResourceUsage SampledDepthStencilAttachment =
        RenderPipelineResourceUsage.SampledTexture |
        RenderPipelineResourceUsage.DepthStencilAttachment |
        RenderPipelineResourceUsage.TransferSource;

    private const RenderPipelineResourceUsage SampledStorageTexture =
        RenderPipelineResourceUsage.SampledTexture |
        RenderPipelineResourceUsage.StorageImage |
        RenderPipelineResourceUsage.TransferSource;

    private const RenderPipelineResourceUsage PrecomputedColorTexture =
        RenderPipelineResourceUsage.SampledTexture |
        RenderPipelineResourceUsage.ColorAttachment |
        RenderPipelineResourceUsage.TransferSource |
        RenderPipelineResourceUsage.TransferDestination;

    private const RenderPipelineResourceUsage BloomColorTexture = PrecomputedColorTexture;

    private const int BloomMaxMipmapLevel = 4;

    internal override ulong BuildResourceFeatureMaskForGenerationKey(XRRenderPipelineInstance instance, XRViewport? viewport)
    {
        DefaultPipelineResourceFeature mask = DefaultPipelineResourceFeature.None;

        if (viewport?.CapturePolicy.UsesMinimalDirectFboPath == true)
            return (ulong)DefaultPipelineResourceFeature.MinimalDirectCapture;

        bool useOpenXrVulkanSafePath = UseOpenXrVulkanDesktopStartupSafePath;
        bool usesStereoResources = UsesStereoResources(instance, viewport);

        if (EnableDeferredMsaa && !useOpenXrVulkanSafePath)
            mask |= DefaultPipelineResourceFeature.DeferredMsaaEnabled;
        if (!useOpenXrVulkanSafePath && ResolveEffectiveAntiAliasingModeForGeneration(instance, viewport) == EAntiAliasingMode.Msaa)
            mask |= DefaultPipelineResourceFeature.MsaaTargetsEnabled;
        bool useForwardPrePassResources = ForwardDepthPrePassEnabled && !useOpenXrVulkanSafePath;
        if (useForwardPrePassResources)
            mask |= DefaultPipelineResourceFeature.ForwardDepthPrePassEnabled;
        if (useForwardPrePassResources && ForwardPrePassSharesGBufferTargets)
            mask |= DefaultPipelineResourceFeature.ForwardPrePassSharesGBufferTargets;
        if (!useOpenXrVulkanSafePath && RuntimeEnableVendorUpscale)
            mask |= DefaultPipelineResourceFeature.VendorUpscalePreferred;
        if (!useOpenXrVulkanSafePath && EnableWeightedBlendedOitPasses)
            mask |= DefaultPipelineResourceFeature.WeightedBlendedOitEnabled;
        if (!useOpenXrVulkanSafePath && ExactTransparencyEnabled)
            mask |= DefaultPipelineResourceFeature.ExactTransparencyEnabled;
        if (!useOpenXrVulkanSafePath && ShouldUseMotionBlur())
            mask |= DefaultPipelineResourceFeature.MotionBlurEnabled;
        if (!useOpenXrVulkanSafePath && ShouldUseDepthOfField())
            mask |= DefaultPipelineResourceFeature.DepthOfFieldEnabled;

        if (useOpenXrVulkanSafePath)
        {
            // This path renders directly into external per-eye OpenXR Vulkan swapchains,
            // where the command graph disables history-based temporal effects.
            mask |= DefaultPipelineResourceFeature.OpenXrVulkanDesktopSafePath;
        }
        else
        {
            // Keep non-safe-path runtime toggles behaving as before: these
            // resources remain available when users flip AO/bloom/temporal
            // settings without rebuilding the command chain.
            mask |= DefaultPipelineResourceFeature.AmbientOcclusionResourcesEnabled;
            // Encode effective AO type in bits 26-29 so mode changes rebuild the generation.
            AmbientOcclusionSettings? aoSettingsForMode = ResolveAmbientOcclusionSettings();
            if (aoSettingsForMode?.Enabled == true)
            {
                int encodedMode = (int)AmbientOcclusionSettings.NormalizeType(aoSettingsForMode.Type) + 1;
                mask |= (DefaultPipelineResourceFeature)((ulong)encodedMode << AoModeFieldShift);
            }
            mask |= DefaultPipelineResourceFeature.BloomResourcesEnabled;
            mask |= DefaultPipelineResourceFeature.TemporalResourcesEnabled;
            if (!usesStereoResources)
            {
                mask |= DefaultPipelineResourceFeature.AtmosphereResourcesEnabled;
                mask |= DefaultPipelineResourceFeature.VolumetricFogResourcesEnabled;
                mask |= DefaultPipelineResourceFeature.DebugVisualizationResourcesEnabled;
            }

            mask |= GlobalIlluminationMode switch
            {
                EGlobalIlluminationMode.PathTracing => DefaultPipelineResourceFeature.RestirGiResourcesEnabled,
                EGlobalIlluminationMode.LightVolumes => DefaultPipelineResourceFeature.LightVolumeGiResourcesEnabled,
                EGlobalIlluminationMode.RadianceCascades => DefaultPipelineResourceFeature.RadianceCascadeGiResourcesEnabled,
                EGlobalIlluminationMode.SurfelGI => DefaultPipelineResourceFeature.SurfelGiResourcesEnabled,
                EGlobalIlluminationMode.VoxelConeTracing => DefaultPipelineResourceFeature.VoxelConeTracingResourcesEnabled,
                _ => DefaultPipelineResourceFeature.None,
            };
        }

        mask |= ForwardDepthNormalPrePassResolution switch
        {
            EDepthNormalPrePassResolution.Half => DefaultPipelineResourceFeature.ForwardDepthPrePassHalfResolution,
            EDepthNormalPrePassResolution.Quarter => DefaultPipelineResourceFeature.ForwardDepthPrePassQuarterResolution,
            _ => DefaultPipelineResourceFeature.None,
        };

        mask |= ResolveGtaoResolutionForGenerationKey() switch
        {
            GroundTruthAmbientOcclusionSettings.EResolution.Full => DefaultPipelineResourceFeature.GtaoFullResolution,
            GroundTruthAmbientOcclusionSettings.EResolution.Quarter => DefaultPipelineResourceFeature.GtaoQuarterResolution,
            _ => DefaultPipelineResourceFeature.None,
        };

        return (ulong)mask;
    }

    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        if ((((DefaultPipelineResourceFeature)builder.Profile.FeatureMask) & DefaultPipelineResourceFeature.MinimalDirectCapture) != 0)
            return;

        DeclareCoreTextures(builder);
        DeclareForwardPlusBuffers(builder);
        DeclareAmbientOcclusionResources(builder);
        DeclareTextureViews(builder);
        DeclareMsaaDeferredResources(builder);
        DeclareForwardPrePassResources(builder);
        DeclareCoreFrameBuffers(builder);
        DeclarePostProcessResources(builder);
        DeclareTransparencyResources(builder);
        DeclareTemporalAndEffectResources(builder);
        DeclareAntiAliasingResources(builder);
        DeclareImportedResources(builder);
        DeclareRemainingDefaultResources(builder);
    }

    private static void DeclareForwardPlusBuffers(RenderPipelineResourceLayoutBuilder builder)
    {
        const uint tileSize = VPRC_ForwardPlusLightCullingPass.TileSize;
        uint tileCountX = (Math.Max(builder.Profile.InternalWidth, 1u) + tileSize - 1u) / tileSize;
        uint tileCountY = (Math.Max(builder.Profile.InternalHeight, 1u) + tileSize - 1u) / tileSize;
        uint viewCount = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);
        uint tileCount = VPRC_ForwardPlusLightCullingPass.ComputeForwardPlusElementCount(checked((int)tileCountX), checked((int)tileCountY), checked((int)viewCount), 1);
        uint visibleCount = checked(tileCount * VPRC_ForwardPlusLightCullingPass.MaxLightsPerTile);

        builder.Buffer(VPRC_ForwardPlusLightCullingPass.LocalLightsBufferName)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)VPRC_ForwardPlusLightCullingPass.MaxLocalLights * VPRC_ForwardPlusLightCullingPass.LocalLightStride, EBufferTarget.ShaderStorageBuffer, EBufferUsage.StreamDraw)
            .Elements(VPRC_ForwardPlusLightCullingPass.LocalLightStride, VPRC_ForwardPlusLightCullingPass.MaxLocalLights)
            .Factory(VPRC_ForwardPlusLightCullingPass.CreateDeclaredLocalLightsBuffer)
            .Add();
        builder.Buffer(VPRC_ForwardPlusLightCullingPass.VisibleIndicesBufferName)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)visibleCount * sizeof(int), EBufferTarget.ShaderStorageBuffer, EBufferUsage.StaticCopy)
            .Elements(sizeof(int), visibleCount)
            .Factory(() => VPRC_ForwardPlusLightCullingPass.CreateDeclaredVisibleIndicesBuffer(visibleCount))
            .Add();
        builder.Buffer(VPRC_ForwardPlusLightCullingPass.TileLightCountsBufferName)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)tileCount * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.StaticCopy)
            .Elements(sizeof(uint), tileCount)
            .Factory(() => VPRC_ForwardPlusLightCullingPass.CreateDeclaredTileLightCountsBuffer(tileCount))
            .Add();
    }

    private static EAntiAliasingMode ResolveEffectiveAntiAliasingModeForGeneration(
        XRRenderPipelineInstance instance,
        XRViewport? viewport)
        => instance.EffectiveAntiAliasingModeThisFrame
            ?? instance.LastSceneCamera?.AntiAliasingModeOverride
            ?? instance.LastRenderingCamera?.AntiAliasingModeOverride
            ?? viewport?.ActiveCamera?.AntiAliasingModeOverride
            ?? RuntimeRenderingHostServices.Current.DefaultAntiAliasingMode;

    private static void DeclareImportedResources(RenderPipelineResourceLayoutBuilder builder)
    {
        DeclareProbeImports(builder);

        const string externalOutput = "$ExternalOutput";
        RenderPipelineExternalTargetKind kind = builder.Profile.ExternalTargetKind;
        if (kind == RenderPipelineExternalTargetKind.None)
            return;

        ExternalRenderResourceOwnership ownership = kind switch
        {
            RenderPipelineExternalTargetKind.Window => ExternalRenderResourceOwnership.Window,
            RenderPipelineExternalTargetKind.ExternalSwapchain => ExternalRenderResourceOwnership.XrRuntime,
            _ => ExternalRenderResourceOwnership.Caller,
        };
        ExternalRenderResourceSynchronization synchronization = kind switch
        {
            RenderPipelineExternalTargetKind.Window => ExternalRenderResourceSynchronization.FrameBoundary,
            RenderPipelineExternalTargetKind.ExternalSwapchain => ExternalRenderResourceSynchronization.AcquireRelease,
            _ => ExternalRenderResourceSynchronization.CallerProvided,
        };

        builder.External(externalOutput)
            .Contract(ExternalRenderResourceKind.FrameBuffer, ownership, synchronization)
            .DebugLabel(kind.ToString())
            .Add();
    }

    private static void DeclareProbeImports(RenderPipelineResourceLayoutBuilder builder)
    {
        string[] textureNames = [LightProbeIrradianceArrayName, LightProbePrefilterArrayName];
        foreach (string name in textureNames)
        {
            builder.External(name)
                .Contract(ExternalRenderResourceKind.Texture, ExternalRenderResourceOwnership.Scene, ExternalRenderResourceSynchronization.FrameBoundary)
                .Add();
        }

        string[] bufferNames =
        [
            LightProbePositionBufferName,
            LightProbeParamBufferName,
            LightProbeTetraBufferName,
            LightProbeGridCellBufferName,
            LightProbeGridIndexBufferName,
        ];
        foreach (string name in bufferNames)
        {
            builder.External(name)
                .Contract(ExternalRenderResourceKind.Buffer, ExternalRenderResourceOwnership.Scene, ExternalRenderResourceSynchronization.FrameBoundary)
                .Add();
        }
    }

    private void DeclareRemainingDefaultResources(RenderPipelineResourceLayoutBuilder builder)
    {
        DeclareRemainingMsaaResources(builder);
        DeclareRemainingForwardPrePassResources(builder);
        DeclarePostProcessExecutionResources(builder);
        DeclareAtmosphereResources(builder);
        DeclareVolumetricFogResources(builder);
        DeclareExactTransparencyResources(builder);
        DeclareGlobalIlluminationResources(builder);
        DeclareDebugVisualizationResources(builder);
    }

    private void DeclareCoreTextures(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, DepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, AlbedoOpacityTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Srgb8Alpha8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Srgb8Alpha8,
            CreateAlbedoOpacityTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, NormalTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateNormalTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, RMSETextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            CreateRMSETexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, TransformIdTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, ESizedInternalFormat.R32ui,
            CreateTransformIdTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, AmbientOcclusionIntensityTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateAmbientOcclusionIntensityTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.ClearOnCommit)
            .Add();

        Texture(builder, LightingAccumTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R11fG11fB10f, EPixelFormat.Rgb, EPixelType.Float, ESizedInternalFormat.R11fG11fB10f,
            CreateLightingAccumTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, DiffuseTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R11fG11fB10f, EPixelFormat.Rgb, EPixelType.Float, ESizedInternalFormat.R11fG11fB10f,
            CreateLightingTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, HDRSceneTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateHDRSceneTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Mips(new RenderResourceMipPolicy(AutoGenerateMipmaps: true, RequireImmutableStorage: true))
            .Add();

        Texture(builder, AutoExposureTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), SampledStorageTexture,
            EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f,
            CreateAutoExposureTexture)
            .RequiresStorageUsage()
            .Add();

        uint brdfLutSize = ResolveBrdfLutSize(builder.Profile);
        Texture(builder, BRDFTextureName, RenderResourceSizePolicy.Absolute(brdfLutSize, brdfLutSize), PrecomputedColorTexture,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateBRDFTexture)
            .Mips(new RenderResourceMipPolicy(AutoGenerateMipmaps: false, RequireImmutableStorage: false))
            .Add();
    }

    private void DeclareAmbientOcclusionResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy gtaoScratchSize = GtaoScratchSizePolicy(builder.Profile);
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, AmbientOcclusionRawTextureName, internalSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateAmbientOcclusionRawTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        Texture(builder, HBAOPlusRawTextureName, internalSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateHBAOPlusRawTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        Texture(builder, HBAOPlusBlurIntermediateTextureName, internalSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateHBAOPlusBlurIntermediateTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        Texture(builder, GTAORawTextureName, gtaoScratchSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateGTAORawTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        Texture(builder, GTAOBlurIntermediateTextureName, gtaoScratchSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateGTAOBlurIntermediateTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        builder.Texture(AmbientOcclusionNoiseTextureName)
            .Size(RenderResourceSizePolicy.Absolute(4u, 4u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.RG, EPixelFormat.Rg, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.Rg16f)
            .Optional()
            .When(UsesAmbientOcclusionResources)
            .Add();

        DeclareAmbientOcclusionFBOs(builder);
    }

    // ── AO mode helpers ────────────────────────────────────────────────────────

    private static int DecodeAoModeFromProfile(RenderPipelineResourceProfile profile)
        => (int)((profile.FeatureMask & AoModeFieldMask) >> AoModeFieldShift);

    private static bool UsesAoMode(RenderPipelineResourceProfile profile, AmbientOcclusionSettings.EType type)
        => UsesAmbientOcclusionResources(profile)
        && DecodeAoModeFromProfile(profile) == (int)AmbientOcclusionSettings.NormalizeType(type) + 1;

    private static bool UsesGTAOMode(RenderPipelineResourceProfile profile)
        => UsesAoMode(profile, AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion);

    private static bool UsesHBAOPlusMode(RenderPipelineResourceProfile profile)
        => UsesAoMode(profile, AmbientOcclusionSettings.EType.HorizonBasedPlus);

    private static bool UsesSpatialHashAOMode(RenderPipelineResourceProfile profile)
        => UsesAoMode(profile, AmbientOcclusionSettings.EType.SpatialHashAmbientOcclusion);

    private static int DecodeCurrentAoMode()
    {
        if (!TryResolveCurrentResourceFeatureMask(out ulong featureMask))
            return 0;
        return (int)((featureMask & AoModeFieldMask) >> AoModeFieldShift);
    }

    // ── AO FBO layout declarations ─────────────────────────────────────────────

    private void DeclareAmbientOcclusionFBOs(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy gtaoScratchSize = GtaoScratchSizePolicy(builder.Profile);

        builder.FrameBuffer(AmbientOcclusionFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, AlbedoOpacityTextureName)
            .Color(1, NormalTextureName)
            .Color(2, RMSETextureName)
            .Color(3, TransformIdTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateAmbientOcclusionGenFbo)
            .When(UsesAmbientOcclusionResources)
            .Add();

        builder.FrameBuffer(AmbientOcclusionBlurFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, AmbientOcclusionRawTextureName)
            .Factory(CreateAmbientOcclusionBlurFbo)
            .When(p => UsesAmbientOcclusionResources(p) && DecodeAoModeFromProfile(p) is 1 or 2 or 4)
            .Add();

        builder.FrameBuffer(AmbientOcclusionBlurFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, AmbientOcclusionIntensityTextureName)
            .Factory(CreateAmbientOcclusionBlurFbo)
            .When(p => UsesAmbientOcclusionResources(p) && DecodeAoModeFromProfile(p) is 0 or 7 or 9)
            .Add();

        builder.FrameBuffer(AmbientOcclusionBlurFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, HBAOPlusRawTextureName)
            .Factory(CreateAmbientOcclusionBlurFbo)
            .When(p => UsesAmbientOcclusionResources(p) && DecodeAoModeFromProfile(p) == 6)
            .Add();

        builder.FrameBuffer(AmbientOcclusionBlurFBOName)
            .Size(gtaoScratchSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, GTAORawTextureName)
            .Factory(CreateAmbientOcclusionBlurFbo)
            .When(p => UsesAmbientOcclusionResources(p) && DecodeAoModeFromProfile(p) == 8)
            .Add();

        builder.FrameBuffer(GTAOBlurIntermediateFBOName)
            .Size(gtaoScratchSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, GTAOBlurIntermediateTextureName)
            .Factory(CreateGtaoBlurIntermediateFbo)
            .When(UsesGTAOMode)
            .Add();

        builder.FrameBuffer(HBAOPlusBlurIntermediateFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, HBAOPlusBlurIntermediateTextureName)
            .Factory(CreateHbaoPlusBlurIntermediateFbo)
            .When(UsesHBAOPlusMode)
            .Add();

        DeclareSpatialHashAOTextures(builder);
        DeclareSpatialHashAOBuffers(builder);
    }

    private void DeclareSpatialHashAOTextures(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);
        const RenderPipelineResourceUsage historyUsage =
            RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage;

        foreach (string name in new[]
        {
            VPRC_SpatialHashAOPass.HistoryAoTextureAName,
            VPRC_SpatialHashAOPass.HistoryAoTextureBName,
        })
        {
            string captured = name;
            builder.Texture(captured)
                .Size(internalSize)
                .Usage(historyUsage)
                .Format(EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat)
                .SizedFormat(ESizedInternalFormat.R16f)
                .Layers(layerCount)
                .StereoCompatible(builder.Profile.Stereo)
                .RequiresStorageUsage()
                .Factory(() => _spatialHashAoProvider!.CreateDeclaredTexture(TryCurrentPipeline!, captured)!)
                .When(UsesSpatialHashAOMode)
                .Add();
        }

        foreach (string name in new[]
        {
            VPRC_SpatialHashAOPass.HistoryDepthTextureAName,
            VPRC_SpatialHashAOPass.HistoryDepthTextureBName,
        })
        {
            string captured = name;
            builder.Texture(captured)
                .Size(internalSize)
                .Usage(historyUsage)
                .Format(EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float)
                .SizedFormat(ESizedInternalFormat.R32f)
                .Layers(layerCount)
                .StereoCompatible(builder.Profile.Stereo)
                .RequiresStorageUsage()
                .Factory(() => _spatialHashAoProvider!.CreateDeclaredTexture(TryCurrentPipeline!, captured)!)
                .When(UsesSpatialHashAOMode)
                .Add();
        }
    }

    private void DeclareSpatialHashAOBuffers(RenderPipelineResourceLayoutBuilder builder)
    {
        uint pixelCount = Math.Max(builder.Profile.InternalWidth * builder.Profile.InternalHeight, 1u);
        uint capacity = VPRC_SpatialHashAOPass.NextPowerOfTwo(pixelCount * VPRC_SpatialHashAOPass.HashMapScale);
        if (capacity == 0) capacity = 1024u;

        builder.Buffer(VPRC_SpatialHashAOPass.HashBufferName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)capacity * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(sizeof(uint), capacity)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(() => _spatialHashAoProvider!.CreateDeclaredBuffer(TryCurrentPipeline!, VPRC_SpatialHashAOPass.HashBufferName)!)
            .When(UsesSpatialHashAOMode)
            .Add();

        builder.Buffer(VPRC_SpatialHashAOPass.HashTimeBufferName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)capacity * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(sizeof(uint), capacity)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(() => _spatialHashAoProvider!.CreateDeclaredBuffer(TryCurrentPipeline!, VPRC_SpatialHashAOPass.HashTimeBufferName)!)
            .When(UsesSpatialHashAOMode)
            .Add();

        builder.Buffer(VPRC_SpatialHashAOPass.SpatialBufferName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)capacity * 2u * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(sizeof(uint) * 2u, capacity)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(() => _spatialHashAoProvider!.CreateDeclaredBuffer(TryCurrentPipeline!, VPRC_SpatialHashAOPass.SpatialBufferName)!)
            .When(UsesSpatialHashAOMode)
            .Add();
    }

    private XRFrameBuffer CreateAmbientOcclusionGenFbo()
    {
        var instance = TryCurrentPipeline
            ?? throw new InvalidOperationException("No active pipeline instance during AO gen FBO materialization.");
        IDeclaredAoResourceProvider provider = SelectAoProviderForCurrentMode()
            ?? throw new InvalidOperationException($"No AO provider registered for mode {DecodeCurrentAoMode()}.");
        return provider.CreateDeclaredFrameBuffer(instance, AmbientOcclusionFBOName);
    }

    private XRFrameBuffer CreateAmbientOcclusionBlurFbo()
    {
        var instance = TryCurrentPipeline
            ?? throw new InvalidOperationException("No active pipeline instance during AO blur FBO materialization.");
        IDeclaredAoResourceProvider provider = SelectAoProviderForCurrentMode()
            ?? throw new InvalidOperationException($"No AO provider registered for mode {DecodeCurrentAoMode()}.");
        return provider.CreateDeclaredFrameBuffer(instance, AmbientOcclusionBlurFBOName);
    }

    private XRFrameBuffer CreateGtaoBlurIntermediateFbo()
    {
        var instance = TryCurrentPipeline
            ?? throw new InvalidOperationException("No active pipeline instance during GTAO intermediate FBO materialization.");
        return (_gtaoAoProvider ?? throw new InvalidOperationException("GTAO provider not registered."))
            .CreateDeclaredFrameBuffer(instance, GTAOBlurIntermediateFBOName);
    }

    private XRFrameBuffer CreateHbaoPlusBlurIntermediateFbo()
    {
        var instance = TryCurrentPipeline
            ?? throw new InvalidOperationException("No active pipeline instance during HBAO+ intermediate FBO materialization.");
        return (_hbaoPlusAoProvider ?? throw new InvalidOperationException("HBAO+ provider not registered."))
            .CreateDeclaredFrameBuffer(instance, HBAOPlusBlurIntermediateFBOName);
    }

    private IDeclaredAoResourceProvider? SelectAoProviderForCurrentMode()
        => GetAoProvider(DecodeCurrentAoMode());

    private IDeclaredAoResourceProvider? GetAoProvider(int modeIndex) => modeIndex switch
    {
        1 => _ssaoAoProvider,
        2 => _mvaoAoProvider,
        4 => _msvoAoProvider,
        6 => _hbaoPlusAoProvider,
        7 => _spatialHashAoProvider,
        8 => _gtaoAoProvider,
        _ => _disabledAoProvider,
    };

    private void DeclareTextureViews(RenderPipelineResourceLayoutBuilder builder)
    {
        uint layerCount = DeclaredLayerCount(builder);
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        DepthStencilView(builder, DepthViewTextureName, DepthStencilTextureName, EDepthStencilFmt.Depth, internalSize, layerCount, CreateDepthViewTexture);
        DepthStencilView(builder, StencilViewTextureName, DepthStencilTextureName, EDepthStencilFmt.Stencil, internalSize, layerCount, CreateStencilViewTexture);
        builder.TextureView(HistoryDepthViewTextureName, HistoryDepthStencilTextureName)
            .Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .LayerRange(0u, layerCount)
            .Target(builder.Profile.Stereo, multisample: false)
            .Factory(CreateHistoryDepthViewTexture)
            .When(UsesTemporalResources)
            .Add();
    }

    private void DeclareMsaaDeferredResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesDeferredMsaa;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint samples = Math.Max(1u, builder.Profile.MsaaSampleCount);

        Texture(builder, MsaaAlbedoOpacityTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Srgb8Alpha8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Srgb8Alpha8,
            CreateMsaaAlbedoOpacityTexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        Texture(builder, MsaaNormalTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateMsaaNormalTexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        Texture(builder, MsaaRMSETextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            CreateMsaaRMSETexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        Texture(builder, MsaaTransformIdTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, ESizedInternalFormat.R32ui,
            CreateMsaaTransformIdTexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        Texture(builder, MsaaDepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateMsaaDepthStencilTexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        Texture(builder, MsaaLightingTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, ESizedInternalFormat.Rgb16f,
            CreateMsaaLightingTexture)
            .Samples(samples)
            .When(predicate)
            .Add();

        builder.TextureView(MsaaDepthViewTextureName, MsaaDepthStencilTextureName)
            .Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .Target(array: false, multisample: true)
            .Factory(CreateMsaaDepthViewTexture)
            .When(predicate)
            .Add();

        Texture(builder, ForwardPassMsaaDepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateForwardPassMsaaDepthStencilTexture)
            .Samples(samples)
            .When(UsesMsaaTargets)
            .Add();

        builder.TextureView(ForwardPassMsaaDepthViewTextureName, ForwardPassMsaaDepthStencilTextureName)
            .Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .Target(array: false, multisample: true)
            .Factory(CreateForwardPassMsaaDepthViewTexture)
            .When(UsesMsaaTargets)
            .Add();
    }

    private void DeclareForwardPrePassResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesForwardPrePass;
        RenderResourceSizePolicy prePassSize = ForwardDepthNormalPrePassSizePolicy();
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, ForwardPrePassDepthStencilTextureName, prePassSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateForwardPrePassDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        Texture(builder, ForwardPrePassNormalTextureName, prePassSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateForwardPrePassNormalTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        Texture(builder, ForwardContactDepthStencilTextureName, prePassSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateForwardContactDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        Texture(builder, ForwardContactNormalTextureName, prePassSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateForwardContactNormalTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        Texture(builder, DeferredGBufferPreForwardDepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateDeferredGBufferPreForwardDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        Texture(builder, DeferredGBufferPreForwardNormalTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateDeferredGBufferPreForwardNormalTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(predicate)
            .Add();

        builder.TextureView(ForwardContactDepthViewTextureName, ForwardContactDepthStencilTextureName)
            .Size(prePassSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .LayerRange(0u, layerCount)
            .Target(builder.Profile.Stereo, multisample: false)
            .Factory(CreateForwardContactDepthViewTexture)
            .When(predicate)
            .Add();
    }

    private void DeclareCoreFrameBuffers(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        builder.FrameBuffer(DeferredGBufferFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, AlbedoOpacityTextureName)
            .Color(1, NormalTextureName)
            .Color(2, RMSETextureName)
            .Color(3, TransformIdTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateDeferredGBufferFBO)
            .Add();

        builder.FrameBuffer(GBufferFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, AmbientOcclusionIntensityTextureName)
            .Factory(CreateGBufferFBO)
            .Add();

        builder.FrameBuffer(LightingAccumFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, LightingAccumTextureName)
            .Factory(CreateLightingAccumFBO)
            .Add();

        builder.FrameBuffer(LightCombineFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(
                AlbedoOpacityTextureName,
                NormalTextureName,
                RMSETextureName,
                AmbientOcclusionIntensityTextureName,
                DepthViewTextureName,
                LightingAccumTextureName,
                BRDFTextureName)
            .Color(0, DiffuseTextureName)
            .Factory(CreateLightCombineFBO)
            .Add();

        builder.FrameBuffer(ForwardPassFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .DependsOn(HDRSceneTextureName)
            .Color(0, HDRSceneTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateForwardPassFBO)
            .Add();

        builder.FrameBuffer(VelocityFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, VelocityTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateVelocityFBO)
            .When(UsesTemporalResources)
            .Add();

        builder.FrameBuffer(MsaaGBufferFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, MsaaAlbedoOpacityTextureName)
            .Color(1, MsaaNormalTextureName)
            .Color(2, MsaaRMSETextureName)
            .Color(3, MsaaTransformIdTextureName)
            .DepthStencil(MsaaDepthStencilTextureName)
            .Factory(CreateMsaaGBufferFBO)
            .When(UsesDeferredMsaa)
            .Add();

        builder.FrameBuffer(MsaaLightingFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, MsaaLightingTextureName)
            .DepthStencil(MsaaDepthStencilTextureName)
            .Factory(CreateMsaaLightingFBO)
            .When(UsesDeferredMsaa)
            .Add();
    }

    private void DeclarePostProcessResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, PostProcessOutputTextureName, internalSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreatePostProcessOutputTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, FinalPostProcessOutputTextureName, internalSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateFinalPostProcessOutputTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, BloomBlurTextureName, internalSize, BloomColorTexture,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateBloomBlurTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Mips(new RenderResourceMipPolicy(
                MipLevelCount: BloomMaxMipmapLevel + 1u,
                AutoGenerateMipmaps: false,
                RequireImmutableStorage: true))
            .When(UsesBloomResources)
            .Add();

        Texture(builder, BloomBlurTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), RenderPipelineResourceUsage.SampledTexture,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateBloomBlurFallbackTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesBloomFallbackResource)
            .Add();

        DeclareBloomFrameBuffers(builder, internalSize);

        Texture(builder, AtmosphereColorTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), RenderPipelineResourceUsage.SampledTexture,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateAtmosphereColorFallbackTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesAtmosphereFallbackResource)
            .Add();

        Texture(builder, VolumetricFogColorTextureName, RenderResourceSizePolicy.Absolute(1u, 1u), RenderPipelineResourceUsage.SampledTexture,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateVolumetricFogColorFallbackTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesVolumetricFogFallbackResource)
            .Add();

        builder.FrameBuffer(PostProcessOutputFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, PostProcessOutputTextureName)
            .Factory(CreatePostProcessOutputFBO)
            .Add();

        builder.FrameBuffer(FinalPostProcessOutputFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, FinalPostProcessOutputTextureName)
            .Factory(CreateFinalPostProcessOutputFBO)
            .Add();
    }

    private void DeclareBloomFrameBuffers(RenderPipelineResourceLayoutBuilder builder, RenderResourceSizePolicy internalSize)
    {
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomMip0FBOName, internalSize, 0);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS1FBOName, internalSize, 1);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS2FBOName, internalSize, 2);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS3FBOName, internalSize, 3);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomDS4FBOName, internalSize, 4);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS3FBOName, internalSize, 3);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS2FBOName, internalSize, 2);
        DeclareBloomFrameBuffer(builder, VPRC_BloomPass.BloomUS1FBOName, internalSize, 1);
    }

    private void DeclareBloomFrameBuffer(RenderPipelineResourceLayoutBuilder builder, string name, RenderResourceSizePolicy size, int mipLevel)
        => builder.FrameBuffer(name)
            .Size(size)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, BloomBlurTextureName, mipLevel)
            .Factory(() => (_bloomProvider ?? throw new InvalidOperationException("Bloom provider not registered."))
                .CreateDeclaredFrameBuffer(TryCurrentPipeline!, name))
            .When(UsesBloomResources)
            .Add();

    private void DeclareTransparencyResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, TransparentSceneCopyTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTransparentSceneCopyTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesTransparencySceneCopyResources)
            .Add();

        Texture(builder, TransparentAccumTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTransparentAccumTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesWeightedBlendedOitResources)
            .Add();

        Texture(builder, TransparentRevealageTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, ESizedInternalFormat.R8,
            CreateTransparentRevealageTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesWeightedBlendedOitResources)
            .Add();

        builder.FrameBuffer(TransparentSceneCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, TransparentSceneCopyTextureName)
            .Factory(CreateTransparentSceneCopyFBO)
            .When(UsesTransparencySceneCopyResources)
            .Add();

        builder.FrameBuffer(DeferredTransparencyBlurFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TransparentSceneCopyTextureName, AlbedoOpacityTextureName, DepthViewTextureName)
            .Color(0, HDRSceneTextureName)
            .Factory(CreateDeferredTransparencyBlurFBO)
            .When(UsesWeightedBlendedOitResources)
            .Add();

        builder.FrameBuffer(TransparentAccumulationFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, TransparentAccumTextureName)
            .Color(1, TransparentRevealageTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateTransparentAccumulationFBO)
            .When(UsesWeightedBlendedOitResources)
            .Add();

        builder.FrameBuffer(TransparentResolveFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TransparentSceneCopyTextureName, TransparentAccumTextureName, TransparentRevealageTextureName)
            .Color(0, HDRSceneTextureName)
            .Factory(CreateTransparentResolveFBO)
            .When(UsesWeightedBlendedOitResources)
            .Add();
    }

    private void DeclareTemporalAndEffectResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, VelocityTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateVelocityTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, HistoryColorTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateHistoryColorTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, HistoryDepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateHistoryDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, TemporalColorInputTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTemporalColorInputTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, TemporalExposureVarianceTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateTemporalExposureVarianceTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, HistoryExposureVarianceTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateHistoryExposureVarianceTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(UsesTemporalResources)
            .Add();

        Texture(builder, MotionBlurTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateMotionBlurTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesMotionBlurResources)
            .Add();

        Texture(builder, DepthOfFieldTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateDepthOfFieldTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(UsesDepthOfFieldResources)
            .Add();

        builder.FrameBuffer(HistoryCaptureFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, HistoryColorTextureName)
            .DepthStencil(HistoryDepthStencilTextureName)
            .Factory(CreateHistoryCaptureFBO)
            .When(UsesTemporalResources)
            .Add();

        builder.FrameBuffer(TemporalInputFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, TemporalColorInputTextureName)
            .Factory(CreateTemporalInputFBO)
            .When(UsesTemporalResources)
            .Add();

        builder.FrameBuffer(TemporalAccumulationFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TemporalColorInputTextureName, HistoryColorTextureName, VelocityTextureName, DepthViewTextureName, HistoryDepthViewTextureName, HistoryExposureVarianceTextureName)
            .Color(0, HDRSceneTextureName)
            .Color(1, TemporalExposureVarianceTextureName)
            .Factory(CreateTemporalAccumulationFBO)
            .When(UsesTemporalResources)
            .Add();

        builder.FrameBuffer(HistoryExposureFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, HistoryExposureVarianceTextureName)
            .Factory(CreateHistoryExposureFBO)
            .When(UsesTemporalResources)
            .Add();

        builder.FrameBuffer(MotionBlurCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, MotionBlurTextureName)
            .Factory(CreateMotionBlurCopyFBO)
            .When(UsesMotionBlurResources)
            .Add();

        builder.FrameBuffer(DepthOfFieldCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, DepthOfFieldTextureName)
            .Factory(CreateDepthOfFieldCopyFBO)
            .When(UsesDepthOfFieldResources)
            .Add();
    }

    private void DeclareAntiAliasingResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy windowSize = RenderResourceSizePolicy.Window();
        uint layerCount = DeclaredLayerCount(builder);
        RenderPipelineResourcePredicate fxaa = static profile =>
            profile.AntiAliasingMode == EAntiAliasingMode.Fxaa;
        RenderPipelineResourcePredicate smaa = static profile =>
            profile.AntiAliasingMode == EAntiAliasingMode.Smaa;
        RenderPipelineResourcePredicate tsr = static profile =>
            UsesTemporalResources(profile) && profile.AntiAliasingMode == EAntiAliasingMode.Tsr;

        Texture(builder, FxaaOutputTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateFxaaOutputTexture)
            .When(fxaa)
            .Add();

        builder.FrameBuffer(FxaaFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, FxaaOutputTextureName)
            .Factory(CreateFxaaFBO)
            .When(fxaa)
            .Add();

        Texture(builder, SmaaEdgeTextureName, windowSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            CreateSmaaEdgeTexture)
            .When(smaa)
            .Add();

        Texture(builder, SmaaBlendTextureName, windowSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            CreateSmaaBlendTexture)
            .When(smaa)
            .Add();

        Texture(builder, SmaaOutputTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateSmaaOutputTexture)
            .When(smaa)
            .Add();

        builder.FrameBuffer(SmaaEdgeFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, SmaaEdgeTextureName)
            .Factory(CreateSmaaEdgeFBO)
            .When(smaa)
            .Add();

        builder.FrameBuffer(SmaaBlendFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(SmaaEdgeTextureName)
            .Color(0, SmaaBlendTextureName)
            .Factory(CreateSmaaBlendFBO)
            .When(smaa)
            .Add();

        builder.FrameBuffer(SmaaFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(SmaaBlendTextureName)
            .Color(0, SmaaOutputTextureName)
            .Factory(CreateSmaaFBO)
            .When(smaa)
            .Add();

        Texture(builder, TsrOutputTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateTsrOutputTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .When(tsr)
            .Add();

        Texture(builder, TsrHistoryColorTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateTsrHistoryColorTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(tsr)
            .Add();

        builder.FrameBuffer(TsrHistoryColorFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, TsrHistoryColorTextureName)
            .Factory(CreateTsrHistoryColorFBO)
            .When(tsr)
            .Add();

        builder.FrameBuffer(TsrUpscaleFBOName)
            .Size(windowSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(FinalPostProcessOutputTextureName, VelocityTextureName, DepthViewTextureName, HistoryDepthViewTextureName, TsrHistoryColorTextureName, StencilViewTextureName)
            .Color(0, TsrOutputTextureName)
            .Factory(CreateTsrUpscaleFBO)
            .When(tsr)
            .Add();
    }

    private void DeclareRemainingMsaaResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate msaa = UsesMsaaTargets;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint samples = Math.Max(1u, builder.Profile.MsaaSampleCount);

        builder.RenderBuffer(ForwardPassMsaaColorRenderBufferName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Storage(ERenderBufferStorage.Rgba16f)
            .Samples(samples)
            .DefaultAttachment(EFrameBufferAttachment.ColorAttachment0)
            .Factory(CreateForwardPassMsaaColorRenderBuffer)
            .When(msaa)
            .Add();

        builder.FrameBuffer(ForwardPassMsaaFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, ForwardPassMsaaColorRenderBufferName)
            .DepthStencil(ForwardPassMsaaDepthStencilTextureName)
            .Factory(CreateForwardPassMsaaFBO)
            .When(msaa)
            .Add();

        builder.QuadMaterial(DepthPreloadFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(DepthViewTextureName)
            .Factory(CreateDepthPreloadFBO)
            .When(UsesForwardOnlyMsaaTargets)
            .Add();

        builder.QuadMaterial(MsaaLightCombineFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(
                MsaaAlbedoOpacityTextureName,
                MsaaNormalTextureName,
                MsaaRMSETextureName,
                AmbientOcclusionIntensityTextureName,
                MsaaDepthViewTextureName,
                MsaaLightingTextureName,
                BRDFTextureName)
            .Factory(CreateMsaaLightCombineFBO)
            .When(UsesDeferredMsaa)
            .Add();
    }

    private void DeclareRemainingForwardPrePassResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesForwardPrePass;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy prePassSize = ForwardDepthNormalPrePassSizePolicy();

        builder.FrameBuffer(ForwardDepthPrePassFBOName)
            .Size(prePassSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, ForwardPrePassNormalTextureName)
            .DepthStencil(ForwardPrePassDepthStencilTextureName)
            .Factory(CreateForwardDepthPrePassFBO)
            .When(predicate)
            .Add();

        builder.FrameBuffer(ForwardDepthPrePassMergeFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, NormalTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateForwardDepthPrePassMergeFBO)
            .When(predicate)
            .Add();

        builder.FrameBuffer(DeferredGBufferPreForwardCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, DeferredGBufferPreForwardNormalTextureName)
            .DepthStencil(DeferredGBufferPreForwardDepthStencilTextureName)
            .Factory(CreateDeferredGBufferPreForwardCopyFBO)
            .When(predicate)
            .Add();

        builder.FrameBuffer(ForwardContactPrePassCopyFBOName)
            .Size(prePassSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, ForwardContactNormalTextureName)
            .DepthStencil(ForwardContactDepthStencilTextureName)
            .Factory(CreateForwardContactPrePassCopyFBO)
            .When(predicate)
            .Add();
    }

    private void DeclarePostProcessExecutionResources(RenderPipelineResourceLayoutBuilder builder)
    {
        builder.QuadMaterial(PostProcessFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(
                HDRSceneTextureName,
                BloomBlurTextureName,
                DepthViewTextureName,
                StencilViewTextureName,
                AutoExposureTextureName,
                AtmosphereColorTextureName,
                VolumetricFogColorTextureName)
            .Factory(CreatePostProcessFBO)
            .Add();

        builder.QuadMaterial(FinalPostProcessFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(PostProcessOutputTextureName)
            .Factory(CreateFinalPostProcessFBO)
            .Add();

        builder.QuadMaterial(MotionBlurFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(MotionBlurTextureName, VelocityTextureName, DepthViewTextureName)
            .Factory(CreateMotionBlurFBO)
            .When(UsesMotionBlurResources)
            .Add();

        builder.QuadMaterial(DepthOfFieldFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(DepthOfFieldTextureName, DepthViewTextureName)
            .Factory(CreateDepthOfFieldFBO)
            .When(UsesDepthOfFieldResources)
            .Add();

        builder.QuadMaterial(SceneCopyFBOName)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(HDRSceneTextureName)
            .Factory(CreateSceneCopyFBO)
            .When(UsesTransparencySceneCopyResources)
            .Add();
    }

    private void DeclareAtmosphereResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesAtmosphereResources;
        RenderResourceSizePolicy full = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy half = RenderResourceSizePolicy.Internal(0.5f);

        DeclareColorTexture(builder, AtmosphereColorTextureName, full, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateAtmosphereColorTexture, predicate);
        DeclareColorTexture(builder, AtmosphereHalfDepthTextureName, half, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f, CreateAtmosphereHalfDepthTexture, predicate);
        DeclareColorTexture(builder, AtmosphereHalfScatterTextureName, half, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateAtmosphereHalfScatterTexture, predicate);
        DeclareColorTexture(builder, AtmosphereHalfTemporalTextureName, half, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateAtmosphereHalfTemporalTexture, predicate);
        Texture(builder, AtmosphereHalfHistoryTextureName, half, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateAtmosphereHalfHistoryTexture)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(predicate)
            .Add();

        DeclareEffectQuad(builder, AtmosphereHalfDepthQuadFBOName, CreateAtmosphereHalfDepthQuadFBO, predicate, DepthViewTextureName);
        DeclareEffectDestination(builder, AtmosphereHalfDepthFBOName, AtmosphereHalfDepthTextureName, half, CreateAtmosphereHalfDepthFBO, predicate);
        DeclareEffectQuad(builder, AtmosphereHalfScatterQuadFBOName, CreateAtmosphereHalfScatterQuadFBO, predicate, AtmosphereHalfDepthTextureName);
        DeclareEffectDestination(builder, AtmosphereHalfScatterFBOName, AtmosphereHalfScatterTextureName, half, CreateAtmosphereHalfScatterFBO, predicate);
        DeclareEffectQuad(builder, AtmosphereReprojectQuadFBOName, CreateAtmosphereReprojectQuadFBO, predicate, AtmosphereHalfScatterTextureName, AtmosphereHalfHistoryTextureName, AtmosphereHalfDepthTextureName);
        DeclareEffectDestination(builder, AtmosphereReprojectFBOName, AtmosphereHalfTemporalTextureName, half, CreateAtmosphereReprojectFBO, predicate);
        DeclareEffectDestination(builder, AtmosphereHistoryFBOName, AtmosphereHalfHistoryTextureName, half, CreateAtmosphereHistoryFBO, predicate, RenderResourceLifetime.Persistent);
        DeclareEffectQuad(builder, AtmosphereUpscaleQuadFBOName, CreateAtmosphereUpscaleQuadFBO, predicate, AtmosphereHalfTemporalTextureName, AtmosphereHalfDepthTextureName, DepthViewTextureName);
        DeclareEffectDestination(builder, AtmosphereUpscaleFBOName, AtmosphereColorTextureName, full, CreateAtmosphereUpscaleFBO, predicate);
    }

    private void DeclareVolumetricFogResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesVolumetricFogResources;
        RenderResourceSizePolicy full = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy half = RenderResourceSizePolicy.Internal(0.5f);

        DeclareColorTexture(builder, VolumetricFogColorTextureName, full, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateVolumetricFogColorTexture, predicate);
        DeclareColorTexture(builder, VolumetricFogHalfDepthTextureName, half, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f, CreateVolumetricFogHalfDepthTexture, predicate);
        DeclareColorTexture(builder, VolumetricFogHalfScatterTextureName, half, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateVolumetricFogHalfScatterTexture, predicate);
        DeclareColorTexture(builder, VolumetricFogHalfTemporalTextureName, half, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, CreateVolumetricFogHalfTemporalTexture, predicate);
        Texture(builder, VolumetricFogHalfHistoryTextureName, half, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateVolumetricFogHalfHistoryTexture)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .When(predicate)
            .Add();

        DeclareEffectQuad(builder, VolumetricFogHalfDepthQuadFBOName, CreateVolumetricFogHalfDepthQuadFBO, predicate, DepthViewTextureName);
        DeclareEffectDestination(builder, VolumetricFogHalfDepthFBOName, VolumetricFogHalfDepthTextureName, half, CreateVolumetricFogHalfDepthFBO, predicate);
        DeclareEffectQuad(builder, VolumetricFogHalfScatterQuadFBOName, CreateVolumetricFogHalfScatterQuadFBO, predicate, VolumetricFogHalfDepthTextureName);
        DeclareEffectDestination(builder, VolumetricFogHalfScatterFBOName, VolumetricFogHalfScatterTextureName, half, CreateVolumetricFogHalfScatterFBO, predicate);
        DeclareEffectQuad(builder, VolumetricFogReprojectQuadFBOName, CreateVolumetricFogReprojectQuadFBO, predicate, VolumetricFogHalfScatterTextureName, VolumetricFogHalfHistoryTextureName, VolumetricFogHalfDepthTextureName);
        DeclareEffectDestination(builder, VolumetricFogReprojectFBOName, VolumetricFogHalfTemporalTextureName, half, CreateVolumetricFogReprojectFBO, predicate);
        DeclareEffectDestination(builder, VolumetricFogHistoryFBOName, VolumetricFogHalfHistoryTextureName, half, CreateVolumetricFogHistoryFBO, predicate, RenderResourceLifetime.Persistent);
        DeclareEffectQuad(builder, VolumetricFogUpscaleQuadFBOName, CreateVolumetricFogUpscaleQuadFBO, predicate, VolumetricFogHalfTemporalTextureName, VolumetricFogHalfDepthTextureName, DepthViewTextureName);
        DeclareEffectDestination(builder, VolumetricFogUpscaleFBOName, VolumetricFogColorTextureName, full, CreateVolumetricFogUpscaleFBO, predicate);
    }

    private void DeclareExactTransparencyResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesExactTransparencyResources;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint nodeCount = ComputePpllNodeCapacity(builder.Profile);

        Texture(builder, PpllHeadPointerTextureName, internalSize, SampledStorageTexture,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt, ESizedInternalFormat.R32ui,
            CreatePpllHeadPointerTexture)
            .RequiresStorageUsage()
            .When(predicate)
            .Add();
        DeclareColorTexture(builder, PpllFragmentCountTextureName, internalSize, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f, CreatePpllFragmentCountTexture, predicate);

        builder.Buffer(PpllNodeBufferName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)nodeCount * PpllNodeStrideBytes, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicCopy)
            .Elements(PpllNodeStrideBytes, nodeCount)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(CreatePpllNodeBuffer)
            .When(predicate)
            .Add();
        builder.Buffer(PpllCounterBufferName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat(2u * sizeof(uint), EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicCopy)
            .Elements(sizeof(uint), 2u)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(CreatePpllCounterBuffer)
            .When(predicate)
            .Add();

        for (int layerIndex = 0; layerIndex < MaxDepthPeelingLayersSupported; layerIndex++)
        {
            int capture = layerIndex;
            string colorName = DepthPeelColorTextureName(capture);
            string depthName = DepthPeelDepthTextureName(capture);
            DeclareColorTexture(builder, colorName, internalSize, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, () => CreateDepthPeelColorTexture(capture), predicate);
            Texture(builder, depthName, internalSize, SampledDepthStencilAttachment,
                EPixelInternalFormat.DepthComponent32, EPixelFormat.DepthComponent, EPixelType.Float, ESizedInternalFormat.DepthComponent32f,
                () => CreateDepthPeelDepthTexture(capture))
                .When(predicate)
                .Add();
            builder.FrameBuffer(DepthPeelLayerFboName(capture))
                .Size(internalSize)
                .Lifetime(RenderResourceLifetime.Transient)
                .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
                .Color(0, colorName)
                .Depth(depthName)
                .Factory(() => CreateDepthPeelLayerFBO(capture))
                .When(predicate)
                .Add();
        }

        builder.FrameBuffer(PpllResolveFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TransparentSceneCopyTextureName, PpllHeadPointerTextureName, PpllNodeBufferName, PpllCounterBufferName)
            .Color(0, HDRSceneTextureName)
            .Color(1, PpllFragmentCountTextureName)
            .Factory(CreatePpllResolveFBO)
            .When(predicate)
            .Add();
        builder.FrameBuffer(DepthPeelingResolveFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(DepthPeelColorTextureName(0), DepthPeelDepthTextureName(0))
            .Color(0, HDRSceneTextureName)
            .Factory(CreateDepthPeelingResolveFBO)
            .When(predicate)
            .Add();
        DeclareEffectQuad(builder, PpllFragmentCountDebugFBOName, CreatePpllFragmentCountDebugFBO, predicate, PpllFragmentCountTextureName);
        DeclareEffectQuad(builder, DepthPeelingDebugFBOName, CreateDepthPeelingDebugFBO, predicate, DepthPeelColorTextureName(0), DepthPeelDepthTextureName(0));
    }

    private void DeclareGlobalIlluminationResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layers = DeclaredLayerCount(builder);

        DeclareGiTextureAndQuad(builder, RestirGITextureName, RestirCompositeFBOName, CreateRestirGITexture, CreateRestirCompositeFBO, UsesRestirGiResources, layers, internalSize);
        DeclareRestirReservoirBuffers(builder);
        DeclareGiTextureAndQuad(builder, LightVolumeGITextureName, LightVolumeCompositeFBOName, CreateLightVolumeGITexture, CreateLightVolumeCompositeFBO, UsesLightVolumeGiResources, layers, internalSize);
        DeclareGiTextureAndQuad(builder, RadianceCascadeGITextureName, RadianceCascadeCompositeFBOName, CreateRadianceCascadeGITexture, CreateRadianceCascadeCompositeFBO, UsesRadianceCascadeGiResources, layers, internalSize, DepthViewTextureName, NormalTextureName);
        DeclareRadianceCascadeHistoryTextures(builder, internalSize, layers);
        DeclareGiTextureAndQuad(builder, SurfelGITextureName, SurfelGICompositeFBOName, CreateSurfelGITexture, CreateSurfelGICompositeFBO, UsesSurfelGiResources, layers, internalSize);
        DeclareSurfelGiBuffers(builder);

        Texture(builder, VoxelConeTracingVolumeTextureName, RenderResourceSizePolicy.Absolute(128u, 128u),
            RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage,
            EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, ESizedInternalFormat.Rgba8,
            CreateVoxelConeTracingVolumeTexture)
            .Mips(new RenderResourceMipPolicy(AutoGenerateMipmaps: true, RequireImmutableStorage: false))
            .RequiresStorageUsage()
            .When(UsesVoxelConeTracingResources)
            .Add();
    }

    private void DeclareRestirReservoirBuffers(RenderPipelineResourceLayoutBuilder builder)
    {
        uint elementCount = checked(Math.Max(1u, builder.Profile.InternalWidth) * Math.Max(1u, builder.Profile.InternalHeight));
        DeclareRestirReservoirBuffer(builder, VPRC_ReSTIRPass.InitialReservoirBufferName, 3u, elementCount);
        DeclareRestirReservoirBuffer(builder, VPRC_ReSTIRPass.TemporalReservoirBufferName, 4u, elementCount);
        DeclareRestirReservoirBuffer(builder, VPRC_ReSTIRPass.SpatialReservoirBufferName, 5u, elementCount);
    }

    private static void DeclareRestirReservoirBuffer(RenderPipelineResourceLayoutBuilder builder, string name, uint bindingIndex, uint elementCount)
        => builder.Buffer(name)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)elementCount * VPRC_ReSTIRPass.ReservoirStride, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(VPRC_ReSTIRPass.ReservoirStride, elementCount)
            .Factory(() => VPRC_ReSTIRPass.CreateDeclaredReservoirBuffer(name, bindingIndex, elementCount))
            .When(UsesRestirGiResources)
            .Add();

    private static void DeclareSurfelGiBuffers(RenderPipelineResourceLayoutBuilder builder)
    {
        DeclareSurfelGiBuffer(builder, VPRC_SurfelGIPass.SurfelBufferName, VPRC_SurfelGIPass.SurfelStride, VPRC_SurfelGIPass.MaxSurfelsConst, VPRC_SurfelGIPass.CreateDeclaredSurfelBuffer);
        DeclareSurfelGiBuffer(builder, VPRC_SurfelGIPass.CounterBufferName, VPRC_SurfelGIPass.ScalarStride, VPRC_SurfelGIPass.CounterCount, VPRC_SurfelGIPass.CreateDeclaredCounterBuffer);
        DeclareSurfelGiBuffer(builder, VPRC_SurfelGIPass.FreeStackBufferName, VPRC_SurfelGIPass.ScalarStride, VPRC_SurfelGIPass.MaxSurfelsConst, VPRC_SurfelGIPass.CreateDeclaredFreeStackBuffer);
        DeclareSurfelGiBuffer(builder, VPRC_SurfelGIPass.GridCountsBufferName, VPRC_SurfelGIPass.ScalarStride, VPRC_SurfelGIPass.GridCellCount, VPRC_SurfelGIPass.CreateDeclaredGridCountsBuffer);
        DeclareSurfelGiBuffer(builder, VPRC_SurfelGIPass.GridIndicesBufferName, VPRC_SurfelGIPass.ScalarStride, VPRC_SurfelGIPass.GridIndexCount, VPRC_SurfelGIPass.CreateDeclaredGridIndicesBuffer);
    }

    private static void DeclareSurfelGiBuffer(RenderPipelineResourceLayoutBuilder builder, string name, uint stride, uint elementCount, Func<XRDataBuffer> factory)
        => builder.Buffer(name)
            .Size(RenderResourceSizePolicy.Internal())
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.StorageBuffer)
            .BufferFormat((ulong)stride * elementCount, EBufferTarget.ShaderStorageBuffer, EBufferUsage.DynamicDraw)
            .Elements(stride, elementCount)
            .Factory(factory)
            .When(UsesSurfelGiResources)
            .Add();

    private void DeclareRadianceCascadeHistoryTextures(RenderPipelineResourceLayoutBuilder builder, RenderResourceSizePolicy size, uint layers)
    {
        DeclareRadianceCascadeHistoryTexture(builder, VPRC_RadianceCascadesPass.HistoryTextureAName, size, layers);
        DeclareRadianceCascadeHistoryTexture(builder, VPRC_RadianceCascadesPass.HistoryTextureBName, size, layers);
    }

    private void DeclareRadianceCascadeHistoryTexture(RenderPipelineResourceLayoutBuilder builder, string name, RenderResourceSizePolicy size, uint layers)
        => Texture(builder, name, size, SampledStorageTexture,
                EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
                () => CreateRadianceCascadeHistoryTexture(name))
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .RequiresStorageUsage()
            .History(RenderResourceHistoryPolicy.ClearOnCommit)
            .When(UsesRadianceCascadeGiResources)
            .Add();

    private XRTexture CreateRadianceCascadeHistoryTexture(string name)
    {
        XRTexture texture = CreateRadianceCascadeGITexture();
        texture.Name = name;
        texture.SamplerName = "gHistory";
        if (texture is XRTexture2D texture2D)
        {
            texture2D.MinFilter = ETexMinFilter.Linear;
            texture2D.MagFilter = ETexMagFilter.Linear;
        }
        else if (texture is XRTexture2DArray textureArray)
        {
            textureArray.MinFilter = ETexMinFilter.Linear;
            textureArray.MagFilter = ETexMagFilter.Linear;
        }
        return texture;
    }

    private void DeclareDebugVisualizationResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourcePredicate predicate = UsesDebugVisualizationResources;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        DeclareColorTexture(builder, FullOverdrawCountTextureName, internalSize, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f, CreateFullOverdrawCountTexture, predicate);
        DeclareEffectDestination(builder, FullOverdrawCountFBOName, FullOverdrawCountTextureName, internalSize, CreateFullOverdrawCountFBO, predicate);
        DeclareEffectQuad(builder, FullOverdrawDebugFBOName, CreateFullOverdrawDebugFBO, predicate, FullOverdrawCountTextureName, PostProcessOutputTextureName);
        DeclareEffectQuad(builder, TransformIdDebugQuadFBOName, CreateTransformIdDebugQuadFBO, predicate, TransformIdTextureName);
        DeclareEffectQuad(builder, TransparentAccumulationDebugFBOName, CreateTransparentAccumulationDebugFBO, UsesWeightedBlendedOitResources, TransparentAccumTextureName);
        DeclareEffectQuad(builder, TransparentRevealageDebugFBOName, CreateTransparentRevealageDebugFBO, UsesWeightedBlendedOitResources, TransparentRevealageTextureName);
        DeclareEffectQuad(builder, TransparentOverdrawDebugFBOName, CreateTransparentOverdrawDebugFBO, UsesWeightedBlendedOitResources, TransparentRevealageTextureName, TransparentAccumTextureName);
    }

    private void DeclareColorTexture(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        RenderResourceSizePolicy size,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedFormat,
        Func<XRTexture> factory,
        RenderPipelineResourcePredicate predicate)
        => Texture(builder, name, size, SampledColorAttachment, internalFormat, pixelFormat, pixelType, sizedFormat, factory)
            .When(predicate)
            .Add();

    private static void DeclareEffectDestination(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        string textureName,
        RenderResourceSizePolicy size,
        Func<XRFrameBuffer> factory,
        RenderPipelineResourcePredicate predicate,
        RenderResourceLifetime lifetime = RenderResourceLifetime.Transient)
        => builder.FrameBuffer(name)
            .Size(size)
            .Lifetime(lifetime)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, textureName)
            .Factory(factory)
            .When(predicate)
            .Add();

    private static void DeclareEffectQuad(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        Func<XRFrameBuffer> factory,
        RenderPipelineResourcePredicate predicate,
        params string[] dependencies)
        => builder.QuadMaterial(name)
            .Lifetime(RenderResourceLifetime.Transient)
            .DependsOn(dependencies)
            .Factory(factory)
            .When(predicate)
            .Add();

    private void DeclareGiTextureAndQuad(
        RenderPipelineResourceLayoutBuilder builder,
        string textureName,
        string quadName,
        Func<XRTexture> textureFactory,
        Func<XRFrameBuffer> quadFactory,
        RenderPipelineResourcePredicate predicate,
        uint layers,
        RenderResourceSizePolicy size,
        params string[] extraDependencies)
    {
        Texture(builder, textureName, size, SampledStorageTexture,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            textureFactory)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .RequiresStorageUsage()
            .When(predicate)
            .Add();

        string[] dependencies = new string[extraDependencies.Length + 1];
        dependencies[0] = textureName;
        Array.Copy(extraDependencies, 0, dependencies, 1, extraDependencies.Length);
        DeclareEffectQuad(builder, quadName, quadFactory, predicate, dependencies);
    }

    private XRTexture CreateAmbientOcclusionIntensityTexture()
    {
        if (Stereo)
        {
            var texture = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                InternalWidth,
                InternalHeight,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            texture.Resizable = false;
            texture.SizedInternalFormat = ESizedInternalFormat.R16f;
            texture.OVRMultiViewParameters = new(0, 2u);
            texture.MinFilter = ETexMinFilter.Nearest;
            texture.MagFilter = ETexMagFilter.Nearest;
            texture.UWrap = ETexWrapMode.ClampToEdge;
            texture.VWrap = ETexWrapMode.ClampToEdge;
            texture.Name = AmbientOcclusionIntensityTextureName;
            texture.SamplerName = AmbientOcclusionIntensityTextureName;
            return texture;
        }

        var aoTexture = XRTexture2D.CreateFrameBufferTexture(
            InternalWidth,
            InternalHeight,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        aoTexture.Resizable = false;
        aoTexture.SizedInternalFormat = ESizedInternalFormat.R16f;
        aoTexture.MinFilter = ETexMinFilter.Nearest;
        aoTexture.MagFilter = ETexMagFilter.Nearest;
        aoTexture.UWrap = ETexWrapMode.ClampToEdge;
        aoTexture.VWrap = ETexWrapMode.ClampToEdge;
        aoTexture.Name = AmbientOcclusionIntensityTextureName;
        aoTexture.SamplerName = AmbientOcclusionIntensityTextureName;
        return aoTexture;
    }

    private XRTexture CreateAmbientOcclusionRawTexture()
        => CreateAmbientOcclusionScratchTexture(
            AmbientOcclusionRawTextureName,
            AmbientOcclusionIntensityTextureName,
            InternalWidth,
            InternalHeight,
            linearFiltering: false);

    private XRTexture CreateHBAOPlusRawTexture()
        => CreateAmbientOcclusionScratchTexture(
            HBAOPlusRawTextureName,
            "HBAOInputTexture",
            InternalWidth,
            InternalHeight,
            linearFiltering: false);

    private XRTexture CreateHBAOPlusBlurIntermediateTexture()
        => CreateAmbientOcclusionScratchTexture(
            HBAOPlusBlurIntermediateTextureName,
            "HBAOInputTexture",
            InternalWidth,
            InternalHeight,
            linearFiltering: false);

    private XRTexture CreateGTAORawTexture()
    {
        (uint width, uint height) = GetDesiredGtaoScratchSize();
        return CreateAmbientOcclusionScratchTexture(
            GTAORawTextureName,
            "GTAOInputTexture",
            width,
            height,
            linearFiltering: false);
    }

    private XRTexture CreateGTAOBlurIntermediateTexture()
    {
        (uint width, uint height) = GetDesiredGtaoScratchSize();
        return CreateAmbientOcclusionScratchTexture(
            GTAOBlurIntermediateTextureName,
            "GTAOInputTexture",
            width,
            height,
            linearFiltering: true);
    }

    private static RenderResourceSizePolicy GtaoScratchSizePolicy(RenderPipelineResourceProfile profile)
    {
        int divisor = GtaoResolutionDivisor(ResolveGtaoResolutionFromFeatureMask(profile.FeatureMask));
        return RenderResourceSizePolicy.Internal(1.0f / divisor);
    }

    private (uint Width, uint Height) GetDesiredGtaoScratchSize()
    {
        int divisor = GtaoResolutionDivisor(ResolveGtaoResolutionForTextureFactory());
        return (
            ScaleDeclaredInternalExtent(InternalWidth, (uint)divisor),
            ScaleDeclaredInternalExtent(InternalHeight, (uint)divisor));
    }

    private static uint ScaleDeclaredInternalExtent(uint extent, uint divisor)
        => (uint)Math.Max(1, (int)MathF.Round(Math.Max(extent, 1u) / (float)Math.Max(divisor, 1u)));

    private GroundTruthAmbientOcclusionSettings.EResolution ResolveGtaoResolutionForGenerationKey()
        => ResolveAmbientOcclusionSettings()?.GroundTruth.Resolution
            ?? GroundTruthAmbientOcclusionSettings.DefaultResolution;

    private GroundTruthAmbientOcclusionSettings.EResolution ResolveGtaoResolutionForTextureFactory()
    {
        if (TryResolveCurrentResourceFeatureMask(out ulong featureMask))
            return ResolveGtaoResolutionFromFeatureMask(featureMask);

        return ResolveGtaoResolutionForGenerationKey();
    }

    private static bool TryResolveCurrentResourceFeatureMask(out ulong featureMask)
    {
        XRRenderPipelineInstance? pipeline = RuntimeRenderingHostServices.Current.CurrentRenderPipelineContext as XRRenderPipelineInstance
            ?? RuntimeEngine.Rendering.State.CurrentRenderingPipeline;

        if (pipeline?.CurrentResourceBuildContext is XRRenderPipelineInstance.ResourceBuildContext context)
        {
            featureMask = context.Key.FeatureMask;
            return true;
        }

        featureMask = 0UL;
        return false;
    }

    private static GroundTruthAmbientOcclusionSettings.EResolution ResolveGtaoResolutionFromFeatureMask(ulong featureMask)
    {
        if ((featureMask & (ulong)DefaultPipelineResourceFeature.GtaoQuarterResolution) != 0)
            return GroundTruthAmbientOcclusionSettings.EResolution.Quarter;

        if ((featureMask & (ulong)DefaultPipelineResourceFeature.GtaoFullResolution) != 0)
            return GroundTruthAmbientOcclusionSettings.EResolution.Full;

        return GroundTruthAmbientOcclusionSettings.DefaultResolution;
    }

    private static int GtaoResolutionDivisor(GroundTruthAmbientOcclusionSettings.EResolution resolution)
    {
        int divisor = (int)resolution;
        return divisor > 0 ? divisor : (int)GroundTruthAmbientOcclusionSettings.DefaultResolution;
    }

    private XRTexture CreateAmbientOcclusionScratchTexture(
        string textureName,
        string samplerName,
        uint width,
        uint height,
        bool linearFiltering)
    {
        var minFilter = linearFiltering ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
        var magFilter = linearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;

        if (Stereo)
        {
            var texture = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                Math.Max(1u, width),
                Math.Max(1u, height),
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            texture.Resizable = false;
            texture.SizedInternalFormat = ESizedInternalFormat.R16f;
            texture.OVRMultiViewParameters = new(0, 2u);
            texture.Name = textureName;
            texture.SamplerName = samplerName;
            texture.MinFilter = minFilter;
            texture.MagFilter = magFilter;
            texture.UWrap = ETexWrapMode.ClampToEdge;
            texture.VWrap = ETexWrapMode.ClampToEdge;
            return texture;
        }

        var aoTexture = XRTexture2D.CreateFrameBufferTexture(
            Math.Max(1u, width),
            Math.Max(1u, height),
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.HalfFloat,
            EFrameBufferAttachment.ColorAttachment0);
        aoTexture.Resizable = false;
        aoTexture.SizedInternalFormat = ESizedInternalFormat.R16f;
        aoTexture.Name = textureName;
        aoTexture.SamplerName = samplerName;
        aoTexture.MinFilter = minFilter;
        aoTexture.MagFilter = magFilter;
        aoTexture.UWrap = ETexWrapMode.ClampToEdge;
        aoTexture.VWrap = ETexWrapMode.ClampToEdge;
        return aoTexture;
    }

    private XRTexture CreateBloomBlurTexture()
    {
        uint width = (uint)Math.Max(1, InternalWidth);
        uint height = (uint)Math.Max(1, InternalHeight);
        int maxMipLevel = Math.Min(BloomMaxMipmapLevel, XRTexture.GetSmallestMipmapLevel(width, height));
        EPixelInternalFormat internalFormat = ResolvePostProcessIntermediateInternalFormat();
        EPixelType pixelType = ResolvePostProcessIntermediatePixelType();
        ESizedInternalFormat sized = ResolvePostProcessIntermediateSizedInternalFormat();

        if (Stereo)
        {
            var texture = XRTexture2DArray.CreateFrameBufferTexture(
                2,
                width,
                height,
                internalFormat,
                EPixelFormat.Rgba,
                pixelType);
            texture.OVRMultiViewParameters = new(0, 2u);
            ConfigureBloomBlurTexture(texture, sized, maxMipLevel);
            return texture;
        }

        var mono = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        ConfigureBloomBlurTexture(mono, sized, maxMipLevel);
        return mono;
    }

    private static void ConfigureBloomBlurTexture(XRTexture2D texture, ESizedInternalFormat sized, int maxMipLevel)
    {
        texture.Resizable = false;
        texture.SizedInternalFormat = sized;
        texture.LargestMipmapLevel = 0;
        texture.SmallestAllowedMipmapLevel = maxMipLevel;
        texture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = BloomBlurTextureName;
        texture.Name = BloomBlurTextureName;
    }

    private static void ConfigureBloomBlurTexture(XRTexture2DArray texture, ESizedInternalFormat sized, int maxMipLevel)
    {
        texture.Resizable = false;
        texture.SizedInternalFormat = sized;
        texture.LargestMipmapLevel = 0;
        texture.SmallestAllowedMipmapLevel = maxMipLevel;
        texture.MinFilter = ETexMinFilter.LinearMipmapLinear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.SamplerName = BloomBlurTextureName;
        texture.Name = BloomBlurTextureName;
    }

    private XRTexture CreateBloomBlurFallbackTexture()
        => CreatePostProcessFallbackTexture(
            BloomBlurTextureName,
            ResolvePostProcessIntermediateInternalFormat(),
            ResolvePostProcessIntermediateSizedInternalFormat(),
            ResolvePostProcessIntermediatePixelType(),
            0.0f, 0.0f, 0.0f, 1.0f);

    private XRTexture CreateAtmosphereColorFallbackTexture()
        => CreatePostProcessFallbackTexture(
            AtmosphereColorTextureName,
            EPixelInternalFormat.Rgba16f,
            ESizedInternalFormat.Rgba16f,
            EPixelType.HalfFloat,
            0.0f, 0.0f, 0.0f, 1.0f);

    private XRTexture CreateVolumetricFogColorFallbackTexture()
        => CreatePostProcessFallbackTexture(
            VolumetricFogColorTextureName,
            EPixelInternalFormat.Rgba16f,
            ESizedInternalFormat.Rgba16f,
            EPixelType.HalfFloat,
            0.0f, 0.0f, 0.0f, 1.0f);

    private XRTexture CreatePostProcessFallbackTexture(
        string textureName,
        EPixelInternalFormat internalFormat,
        ESizedInternalFormat sizedInternalFormat,
        EPixelType pixelType,
        float r,
        float g,
        float b,
        float a)
    {
        byte[] pixelData = CreateRgbaPixelData(pixelType, r, g, b, a);
        if (Stereo)
        {
            var array = new XRTexture2DArray(
                2u,
                1u,
                1u,
                internalFormat,
                EPixelFormat.Rgba,
                pixelType,
                allocateData: false)
            {
                Resizable = false,
                SizedInternalFormat = sizedInternalFormat,
                AutoGenerateMipmaps = false,
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Name = textureName,
                SamplerName = textureName,
                OVRMultiViewParameters = new(0, 2u),
            };
            for (int layer = 0; layer < array.Textures.Length; layer++)
                array.Textures[layer].Mipmaps[0].Data = new DataSource((byte[])pixelData.Clone());
            return array;
        }

        var texture = new XRTexture2D(
            1u,
            1u,
            internalFormat,
            EPixelFormat.Rgba,
            pixelType)
        {
            Resizable = false,
            SizedInternalFormat = sizedInternalFormat,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            Name = textureName,
            SamplerName = textureName
        };
        texture.Mipmaps[0].Data = new DataSource(pixelData);
        return texture;
    }

    /// <summary>
    /// Creates an RGBA pixel data array from the given float values based on the specified pixel type.
    /// </summary>
    /// <param name="pixelType">The pixel type to use for the RGBA data.</param>
    /// <param name="r">The red component of the pixel.</param>
    /// <param name="g">The green component of the pixel.</param>
    /// <param name="b">The blue component of the pixel.</param>
    /// <param name="a">The alpha component of the pixel.</param>
    /// <returns>A byte array representing the RGBA pixel data.</returns>
    private static byte[] CreateRgbaPixelData(EPixelType pixelType, float r, float g, float b, float a)
        => pixelType switch
        {
            EPixelType.HalfFloat => CreateHalfFloatRgbaPixelData(r, g, b, a),
            EPixelType.Float => CreateFloatRgbaPixelData(r, g, b, a),
            EPixelType.UnsignedByte => CreateUnsignedByteRgbaPixelData(r, g, b, a),
            EPixelType.Byte => CreateByteRgbaPixelData(r, g, b, a),
            _ => CreateHalfFloatRgbaPixelData(r, g, b, a),
        };

    /// <summary>
    /// Creates a half-float RGBA pixel data array from the given float values.
    /// </summary>
    /// <param name="r">The red component of the pixel.</param>
    /// <param name="g">The green component of the pixel.</param>
    /// <param name="b">The blue component of the pixel.</param>
    /// <param name="a">The alpha component of the pixel.</param>
    /// <returns>A byte array representing the RGBA pixel data.</returns>
    private static byte[] CreateHalfFloatRgbaPixelData(float r, float g, float b, float a)
    {
        byte[] data = new byte[sizeof(ushort) * 4];
        WriteHalf(data.AsSpan(0, sizeof(ushort)), r);
        WriteHalf(data.AsSpan(sizeof(ushort), sizeof(ushort)), g);
        WriteHalf(data.AsSpan(sizeof(ushort) * 2, sizeof(ushort)), b);
        WriteHalf(data.AsSpan(sizeof(ushort) * 3, sizeof(ushort)), a);
        return data;
    }

    /// <summary>
    /// Writes a half-float value to the specified destination span as little-endian bytes.
    /// </summary>
    /// <param name="destination">The destination span to write the half-float value to.</param>
    /// <param name="value">The half-float value to write.</param>
    /// <remarks>
    /// The value is converted to a 16-bit half-float representation and written as little-endian bytes.
    /// </remarks>
    private static void WriteHalf(Span<byte> destination, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(destination, BitConverter.HalfToUInt16Bits((Half)value));

    /// <summary>
    /// Creates a float RGBA pixel data array from the given float values.
    /// </summary>
    /// <param name="r">The red component of the pixel.</param>
    /// <param name="g">The green component of the pixel.</param>
    /// <param name="b">The blue component of the pixel.</param>
    /// <param name="a">The alpha component of the pixel.</param>
    /// <returns>A byte array representing the RGBA pixel data.</returns>
    private static byte[] CreateFloatRgbaPixelData(float r, float g, float b, float a)
    {
        byte[] data = new byte[sizeof(float) * 4];
        WriteFloat(data.AsSpan(0, sizeof(float)), r);
        WriteFloat(data.AsSpan(sizeof(float), sizeof(float)), g);
        WriteFloat(data.AsSpan(sizeof(float) * 2, sizeof(float)), b);
        WriteFloat(data.AsSpan(sizeof(float) * 3, sizeof(float)), a);
        return data;
    }

    /// <summary>
    /// Writes a float value to the specified destination span as little-endian bytes.
    /// </summary>
    /// <param name="destination">The destination span to write the float value to.</param>
    /// <param name="value">The float value to write.</param>
    private static void WriteFloat(Span<byte> destination, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(destination, BitConverter.SingleToUInt32Bits(value));

    /// <summary>
    /// Creates a float RGBA pixel data array from the given float values.
    /// </summary>
    /// <param name="r">The red component of the pixel.</param>
    /// <param name="g">The green component of the pixel.</param>
    /// <param name="b">The blue component of the pixel.</param>
    /// <param name="a">The alpha component of the pixel.</param>
    /// <returns>A byte array representing the RGBA pixel data.</returns>
    private static byte[] CreateUnsignedByteRgbaPixelData(float r, float g, float b, float a)
        =>
        [
            ToByte(r),
            ToByte(g),
            ToByte(b),
            ToByte(a),
        ];

    /// <summary>
    /// Creates a byte RGBA pixel data array from the given float values.
    /// </summary>
    /// <param name="r">The red component of the pixel.</param>
    /// <param name="g">The green component of the pixel.</param>
    /// <param name="b">The blue component of the pixel.</param>
    /// <param name="a">The alpha component of the pixel.</param>
    /// <returns>A byte array representing the RGBA pixel data.</returns>
    private static byte[] CreateByteRgbaPixelData(float r, float g, float b, float a)
        =>
        [
            unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(r * sbyte.MaxValue), sbyte.MinValue, sbyte.MaxValue)),
            unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(g * sbyte.MaxValue), sbyte.MinValue, sbyte.MaxValue)),
            unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(b * sbyte.MaxValue), sbyte.MinValue, sbyte.MaxValue)),
            unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(a * sbyte.MaxValue), sbyte.MinValue, sbyte.MaxValue)),
        ];

    /// <summary>
    /// Converts a float value to a byte, clamping it to the valid range.
    /// </summary>
    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value * byte.MaxValue), byte.MinValue, byte.MaxValue);

    /// <summary>
    /// Creates a texture resource in the render pipeline layout.
    /// </summary>
    private RenderPipelineResourceLayoutBuilder.TextureSpecBuilder Texture(
        RenderPipelineResourceLayoutBuilder builder,
        string name,
        RenderResourceSizePolicy sizePolicy,
        RenderPipelineResourceUsage usage,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedInternalFormat,
        Func<XRTexture> factory)
        => builder.Texture(name)
            .Size(sizePolicy)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(usage)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedInternalFormat)
            .Factory(factory);

    private static void DepthStencilView(
        RenderPipelineResourceLayoutBuilder builder,
        string viewName,
        string sourceName,
        EDepthStencilFmt aspect,
        RenderResourceSizePolicy sizePolicy,
        uint layerCount,
        Func<XRTexture> factory)
        => builder.TextureView(viewName, sourceName)
            .Size(sizePolicy)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(aspect)
            .LayerRange(0u, layerCount)
            .Target(builder.Profile.Stereo, multisample: false)
            .Factory(factory)
            .Add();

    private static uint DeclaredLayerCount(RenderPipelineResourceLayoutBuilder builder)
        => builder.Profile.Stereo ? 2u : 1u;

    private bool UsesDeferredMsaa(RenderPipelineResourceProfile profile)
        => EnableDeferredMsaa
        && !profile.Stereo
        && !UsesOpenXrVulkanDesktopSafePath(profile)
        && profile.AntiAliasingMode == EAntiAliasingMode.Msaa
        && profile.MsaaSampleCount > 1u;

    private static bool UsesMsaaTargets(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.MsaaTargetsEnabled) != 0
        && !profile.Stereo
        && !UsesOpenXrVulkanDesktopSafePath(profile)
        && profile.AntiAliasingMode == EAntiAliasingMode.Msaa
        && profile.MsaaSampleCount > 1u;

    private bool UsesForwardOnlyMsaaTargets(RenderPipelineResourceProfile profile)
        => UsesMsaaTargets(profile) && !UsesDeferredMsaa(profile);

    private bool UsesForwardPrePass(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.ForwardDepthPrePassEnabled) != 0
        && !UsesOpenXrVulkanDesktopSafePath(profile);

    private static bool UsesWeightedBlendedOitResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.WeightedBlendedOitEnabled) != 0;

    private static bool UsesExactTransparencyResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.ExactTransparencyEnabled) != 0;

    private static bool UsesTransparencySceneCopyResources(RenderPipelineResourceProfile profile)
        => UsesWeightedBlendedOitResources(profile) || UsesExactTransparencyResources(profile);

    private static bool UsesOpenXrVulkanDesktopSafePath(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.OpenXrVulkanDesktopSafePath) != 0;

    private static uint ResolveBrdfLutSize(RenderPipelineResourceProfile profile)
        => UsesOpenXrVulkanDesktopSafePath(profile)
            ? OpenXrVulkanSafePathBrdfLutSize
            : DefaultBrdfLutSize;

    private static bool UsesAmbientOcclusionResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.AmbientOcclusionResourcesEnabled) != 0;

    private static bool UsesBloomResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.BloomResourcesEnabled) != 0;

    private static bool UsesBloomFallbackResource(RenderPipelineResourceProfile profile)
        => !UsesBloomResources(profile);

    private static bool UsesAtmosphereFallbackResource(RenderPipelineResourceProfile profile)
        => !UsesAtmosphereResources(profile);

    private static bool UsesVolumetricFogFallbackResource(RenderPipelineResourceProfile profile)
        => !UsesVolumetricFogResources(profile);

    private static bool UsesTemporalResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.TemporalResourcesEnabled) != 0;

    private static bool UsesMotionBlurResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.MotionBlurEnabled) != 0;

    private static bool UsesDepthOfFieldResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.DepthOfFieldEnabled) != 0;

    private static bool UsesAtmosphereResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.AtmosphereResourcesEnabled) != 0;

    private static bool UsesVolumetricFogResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.VolumetricFogResourcesEnabled) != 0;

    private static bool UsesRestirGiResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.RestirGiResourcesEnabled) != 0;

    private static bool UsesLightVolumeGiResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.LightVolumeGiResourcesEnabled) != 0;

    private static bool UsesRadianceCascadeGiResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.RadianceCascadeGiResourcesEnabled) != 0;

    private static bool UsesSurfelGiResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.SurfelGiResourcesEnabled) != 0;

    private static bool UsesVoxelConeTracingResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.VoxelConeTracingResourcesEnabled) != 0;

    private static bool UsesDebugVisualizationResources(RenderPipelineResourceProfile profile)
        => (profile.FeatureMask & (ulong)DefaultPipelineResourceFeature.DebugVisualizationResourcesEnabled) != 0;

    private RenderResourceSizePolicy ForwardDepthNormalPrePassSizePolicy()
        => RenderResourceSizePolicy.Internal(ForwardDepthNormalPrePassResolution switch
        {
            EDepthNormalPrePassResolution.Half => 0.5f,
            EDepthNormalPrePassResolution.Quarter => 0.25f,
            _ => 1.0f,
        });
}
