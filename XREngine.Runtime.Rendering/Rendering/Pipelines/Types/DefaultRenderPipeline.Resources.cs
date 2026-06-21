using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    [Flags]
    private enum DefaultPipelineResourceFeature : ulong
    {
        None = 0,
        DeferredMsaaEnabled = 1UL << 0,
        ForwardDepthPrePassEnabled = 1UL << 1,
        ForwardPrePassSharesGBufferTargets = 1UL << 2,
        ForwardDepthPrePassHalfResolution = 1UL << 3,
        ForwardDepthPrePassQuarterResolution = 1UL << 4,
        VendorUpscalePreferred = 1UL << 5,
        GtaoFullResolution = 1UL << 6,
        GtaoQuarterResolution = 1UL << 7,
    }

    private const RenderPipelineResourceUsage SampledColorAttachment =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment;

    private const RenderPipelineResourceUsage SampledDepthStencilAttachment =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.DepthStencilAttachment;

    private const RenderPipelineResourceUsage SampledStorageTexture =
        RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage;

    private const RenderPipelineResourceUsage PrecomputedColorTexture =
        RenderPipelineResourceUsage.SampledTexture |
        RenderPipelineResourceUsage.ColorAttachment |
        RenderPipelineResourceUsage.TransferSource |
        RenderPipelineResourceUsage.TransferDestination;

    private const RenderPipelineResourceUsage BloomColorTexture = PrecomputedColorTexture;

    private const int BloomMaxMipmapLevel = 4;

    internal ulong BuildResourceFeatureMaskForGenerationKey()
    {
        DefaultPipelineResourceFeature mask = DefaultPipelineResourceFeature.None;

        if (EnableDeferredMsaa)
            mask |= DefaultPipelineResourceFeature.DeferredMsaaEnabled;
        if (ForwardDepthPrePassEnabled)
            mask |= DefaultPipelineResourceFeature.ForwardDepthPrePassEnabled;
        if (ForwardPrePassSharesGBufferTargets)
            mask |= DefaultPipelineResourceFeature.ForwardPrePassSharesGBufferTargets;
        if (RuntimeEnableVendorUpscale)
            mask |= DefaultPipelineResourceFeature.VendorUpscalePreferred;

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
        DeclareCoreTextures(builder);
        DeclareAmbientOcclusionResources(builder);
        DeclareTextureViews(builder);
        DeclareMsaaDeferredResources(builder);
        DeclareForwardPrePassResources(builder);
        DeclareCoreFrameBuffers(builder);
        DeclarePostProcessResources(builder);
        DeclareTransparencyResources(builder);
        DeclareTemporalAndEffectResources(builder);
        DeclareAntiAliasingResources(builder);
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

        Texture(builder, BRDFTextureName, RenderResourceSizePolicy.Absolute(2048u, 2048u), PrecomputedColorTexture,
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
            .Add();

        Texture(builder, HBAOPlusRawTextureName, internalSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateHBAOPlusRawTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, HBAOPlusBlurIntermediateTextureName, internalSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateHBAOPlusBlurIntermediateTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, GTAORawTextureName, gtaoScratchSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateGTAORawTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, GTAOBlurIntermediateTextureName, gtaoScratchSize, PrecomputedColorTexture,
            EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f,
            CreateGTAOBlurIntermediateTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        builder.Texture(AmbientOcclusionNoiseTextureName)
            .Size(RenderResourceSizePolicy.Absolute(4u, 4u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.RG, EPixelFormat.Rg, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.Rg16f)
            .Optional()
            .Add();
    }

    private void DeclareTextureViews(RenderPipelineResourceLayoutBuilder builder)
    {
        uint layerCount = DeclaredLayerCount(builder);
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();

        DepthStencilView(builder, DepthViewTextureName, DepthStencilTextureName, EDepthStencilFmt.Depth, internalSize, layerCount, CreateDepthViewTexture);
        DepthStencilView(builder, StencilViewTextureName, DepthStencilTextureName, EDepthStencilFmt.Stencil, internalSize, layerCount, CreateStencilViewTexture);
        DepthStencilView(builder, HistoryDepthViewTextureName, HistoryDepthStencilTextureName, EDepthStencilFmt.Depth, internalSize, layerCount, CreateHistoryDepthViewTexture);
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
            .When(predicate)
            .Add();

        builder.TextureView(ForwardPassMsaaDepthViewTextureName, ForwardPassMsaaDepthStencilTextureName)
            .Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth24Stencil8)
            .DepthStencilAspect(EDepthStencilFmt.Depth)
            .Target(array: false, multisample: true)
            .Factory(CreateForwardPassMsaaDepthViewTexture)
            .When(predicate)
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

    private void DeclareTransparencyResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layerCount = DeclaredLayerCount(builder);

        Texture(builder, TransparentSceneCopyTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTransparentSceneCopyTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, TransparentAccumTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTransparentAccumTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, TransparentRevealageTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, ESizedInternalFormat.R8,
            CreateTransparentRevealageTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        builder.FrameBuffer(TransparentSceneCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, TransparentSceneCopyTextureName)
            .Factory(CreateTransparentSceneCopyFBO)
            .Add();

        builder.FrameBuffer(DeferredTransparencyBlurFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TransparentSceneCopyTextureName, AlbedoOpacityTextureName, DepthViewTextureName)
            .Color(0, HDRSceneTextureName)
            .Factory(CreateDeferredTransparencyBlurFBO)
            .Add();

        builder.FrameBuffer(TransparentAccumulationFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, TransparentAccumTextureName)
            .Color(1, TransparentRevealageTextureName)
            .DepthStencil(DepthStencilTextureName)
            .Factory(CreateTransparentAccumulationFBO)
            .Add();

        builder.FrameBuffer(TransparentResolveFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TransparentSceneCopyTextureName, TransparentAccumTextureName, TransparentRevealageTextureName)
            .Color(0, HDRSceneTextureName)
            .Factory(CreateTransparentResolveFBO)
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
            .Add();

        Texture(builder, HistoryColorTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateHistoryColorTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .Add();

        Texture(builder, HistoryDepthStencilTextureName, internalSize, SampledDepthStencilAttachment,
            EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8,
            CreateHistoryDepthStencilTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .Add();

        Texture(builder, TemporalColorInputTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateTemporalColorInputTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, TemporalExposureVarianceTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateTemporalExposureVarianceTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, HistoryExposureVarianceTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            CreateHistoryExposureVarianceTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .History(RenderResourceHistoryPolicy.SeedFromCurrentFrame)
            .Add();

        Texture(builder, MotionBlurTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateMotionBlurTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        Texture(builder, DepthOfFieldTextureName, internalSize, SampledColorAttachment,
            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f,
            CreateDepthOfFieldTexture)
            .Layers(layerCount)
            .StereoCompatible(builder.Profile.Stereo)
            .Add();

        builder.FrameBuffer(HistoryCaptureFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .Color(0, HistoryColorTextureName)
            .DepthStencil(HistoryDepthStencilTextureName)
            .Factory(CreateHistoryCaptureFBO)
            .Add();

        builder.FrameBuffer(TemporalInputFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, TemporalColorInputTextureName)
            .Factory(CreateTemporalInputFBO)
            .Add();

        builder.FrameBuffer(TemporalAccumulationFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .DependsOn(TemporalColorInputTextureName, HistoryColorTextureName, VelocityTextureName, DepthViewTextureName, HistoryDepthViewTextureName, HistoryExposureVarianceTextureName)
            .Color(0, HDRSceneTextureName)
            .Color(1, TemporalExposureVarianceTextureName)
            .Factory(CreateTemporalAccumulationFBO)
            .Add();

        builder.FrameBuffer(HistoryExposureFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, HistoryExposureVarianceTextureName)
            .Factory(CreateHistoryExposureFBO)
            .Add();

        builder.FrameBuffer(MotionBlurCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, MotionBlurTextureName)
            .Factory(CreateMotionBlurCopyFBO)
            .Add();

        builder.FrameBuffer(DepthOfFieldCopyFBOName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, DepthOfFieldTextureName)
            .Factory(CreateDepthOfFieldCopyFBO)
            .Add();
    }

    private void DeclareAntiAliasingResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy windowSize = RenderResourceSizePolicy.Window();
        RenderPipelineResourcePredicate fxaa = static profile => profile.AntiAliasingMode == EAntiAliasingMode.Fxaa;
        RenderPipelineResourcePredicate tsr = static profile => profile.AntiAliasingMode == EAntiAliasingMode.Tsr;

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

        Texture(builder, TsrOutputTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateTsrOutputTexture)
            .When(tsr)
            .Add();

        Texture(builder, TsrHistoryColorTextureName, windowSize, SampledColorAttachment,
            ResolvePostProcessIntermediateInternalFormat(), EPixelFormat.Rgba, ResolvePostProcessIntermediatePixelType(), ResolvePostProcessIntermediateSizedInternalFormat(),
            CreateTsrHistoryColorTexture)
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
            Math.Max(1u, InternalWidth / (uint)divisor),
            Math.Max(1u, InternalHeight / (uint)divisor));
    }

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
        && profile.AntiAliasingMode == EAntiAliasingMode.Msaa
        && profile.MsaaSampleCount > 1u;

    private bool UsesForwardPrePass(RenderPipelineResourceProfile profile)
        => ForwardDepthPrePassEnabled;

    private RenderResourceSizePolicy ForwardDepthNormalPrePassSizePolicy()
        => RenderResourceSizePolicy.Internal(ForwardDepthNormalPrePassResolution switch
        {
            EDepthNormalPrePassResolution.Half => 0.5f,
            EDepthNormalPrePassResolution.Quarter => 0.25f,
            _ => 1.0f,
        });
}
