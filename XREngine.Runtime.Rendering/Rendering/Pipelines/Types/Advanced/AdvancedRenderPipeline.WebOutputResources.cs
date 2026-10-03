using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private const string WebPostProcessNeutralTextureName = "AdvancedWebPostProcessNeutral";

    /// <summary>Realizes the selected mono output graph against the canonical native scene targets.</summary>
    private void DeclareAdvancedWebPostResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        RenderResourceSizePolicy windowSize = RenderResourceSizePolicy.Window();
        // One ABI-neutral color input serves inactive samplers in the complete
        // post program; disabled effects own no execution targets or histories.
        builder.Texture(WebPostProcessNeutralTextureName)
            .Size(RenderResourceSizePolicy.Absolute(1u, 1u))
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .Format(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte)
            .SizedFormat(ESizedInternalFormat.Rgba8)
            .Factory(static () => new XRTexture2D(1, 1, ColorF4.Transparent)
            {
                Name = WebPostProcessNeutralTextureName, Resizable = false,
                AutoGenerateMipmaps = false, MinFilter = ETexMinFilter.Nearest,
                MagFilter = ETexMagFilter.Nearest, UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            }).Add();

        builder.TextureView(DepthStencilTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Usage(RenderPipelineResourceUsage.DepthStencilAttachment | RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0, 1).Target(array: false, multisample: false)
            .Factory(CreateAdvancedVisibilityDepthStencilAlias).Add();
        builder.TextureView(DepthViewTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture).DepthStencilAspect(EDepthStencilFmt.Depth)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0, 1).Target(array: false, multisample: false)
            .Factory(CreateAdvancedVisibilityDepthView).Add();
        builder.TextureView(StencilViewTextureName, AdvancedVisibilityResourceNames.DepthStencil).Size(internalSize)
            .Usage(RenderPipelineResourceUsage.SampledTexture).DepthStencilAspect(EDepthStencilFmt.Stencil)
            .SizedFormat(ESizedInternalFormat.Depth32fStencil8).LayerRange(0, 1).Target(array: false, multisample: false)
            .Factory(CreateAdvancedVisibilityStencilView).Add();

        DeclareAdvancedWebColor(builder, PostProcessOutputTextureName, internalSize);
        DeclareAdvancedWebColor(builder, FinalPostProcessOutputTextureName, internalSize);
        DeclareLatePostDestination(builder, PostProcessOutputFBOName, PostProcessOutputTextureName, internalSize, CreatePostProcessOutputFBO);
        DeclareLatePostDestination(builder, FinalPostProcessOutputFBOName, FinalPostProcessOutputTextureName, internalSize, CreateFinalPostProcessOutputFBO);
        var post = builder.QuadMaterial(PostProcessFBOName)
            .DependsOn(HDRSceneTextureName, DepthViewTextureName, StencilViewTextureName,
                AdvancedVisibilityResourceNames.Metadata, WebPostProcessNeutralTextureName);

        if (HasAdvancedWebFeature(profile, WebBloomFeatureBit))
        {
            uint maxExtent = Math.Max(profile.InternalWidth, profile.InternalHeight);
            uint mipCount = 1;
            while (maxExtent > 1 && mipCount < 5) { maxExtent >>= 1; mipCount++; }
            DeclareAdvancedWebColor(builder, BloomBlurTextureName, internalSize, mipCount);
            DeclareAdvancedBloomFrameBuffers(builder, internalSize);
            post.DependsOn(BloomBlurTextureName);
        }
        if (HasAdvancedWebFeature(profile, WebAutoExposureFeatureBit))
        {
            builder.Texture(AutoExposureTextureName).Size(RenderResourceSizePolicy.Absolute(1, 1))
                .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
                .Format(EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float).SizedFormat(ESizedInternalFormat.R32f)
                .RequiresStorageUsage(true).History(RenderResourceHistoryPolicy.PreserveWhenCompatible)
                .Factory(() => CreateLatePostColor(AutoExposureTextureName, 1, 1, 1, 1,
                    EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f)).Add();
            post.DependsOn(AutoExposureTextureName);
        }
        post.Factory(CreateAdvancedWebPostProcessFbo).Add();
        builder.QuadMaterial(FinalPostProcessFBOName).DependsOn(PostProcessOutputTextureName)
            .Factory(CreateAdvancedWebFinalPostProcessFbo).Add();

        if (HasAdvancedWebFeature(profile, WebMotionBlurFeatureBit))
        {
            DeclareAdvancedWebColor(builder, MotionBlurTextureName, internalSize);
            DeclareLatePostDestination(builder, MotionBlurCopyFBOName, MotionBlurTextureName, internalSize, CreateMotionBlurCopyFBO);
            builder.QuadMaterial(MotionBlurFBOName).DependsOn(MotionBlurTextureName, VelocityTextureName, DepthViewTextureName)
                .Factory(CreateAdvancedWebMotionBlurFbo).Add();
        }
        if (HasAdvancedWebFeature(profile, WebDepthOfFieldFeatureBit))
        {
            DeclareAdvancedWebColor(builder, DepthOfFieldTextureName, internalSize);
            DeclareLatePostDestination(builder, DepthOfFieldCopyFBOName, DepthOfFieldTextureName, internalSize, CreateDepthOfFieldCopyFBO);
            builder.QuadMaterial(DepthOfFieldFBOName).DependsOn(DepthOfFieldTextureName, DepthViewTextureName)
                .Factory(CreateAdvancedWebDepthOfFieldFbo).Add();
        }
        if (profile.AntiAliasingMode == EAntiAliasingMode.Fxaa)
        {
            DeclareAdvancedWebColor(builder, FxaaOutputTextureName, windowSize);
            DeclareLatePostDestination(builder, FxaaFBOName, FxaaOutputTextureName, windowSize, CreateFxaaFBO);
        }
        if (profile.AntiAliasingMode == EAntiAliasingMode.Smaa)
        {
            DeclareAdvancedWebColor(builder, SmaaEdgeTextureName, windowSize, rgba8: true);
            DeclareAdvancedWebColor(builder, SmaaBlendTextureName, windowSize, rgba8: true);
            DeclareAdvancedWebColor(builder, SmaaOutputTextureName, windowSize);
            DeclareLatePostDestination(builder, SmaaEdgeFBOName, SmaaEdgeTextureName, windowSize, CreateSmaaEdgeFBO);
            DeclareLatePostDestination(builder, SmaaBlendFBOName, SmaaBlendTextureName, windowSize, CreateSmaaBlendFBO);
            DeclareLatePostDestination(builder, SmaaFBOName, SmaaOutputTextureName, windowSize, CreateSmaaFBO);
        }
    }

    private void DeclareAdvancedWebColor(RenderPipelineResourceLayoutBuilder builder, string name,
        RenderResourceSizePolicy size, uint mips = 1, bool rgba8 = false)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        uint width = ResolveLatePostExtent(size, profile.DisplayWidth, profile.InternalWidth, size.Width, size.ScaleX);
        uint height = ResolveLatePostExtent(size, profile.DisplayHeight, profile.InternalHeight, size.Height, size.ScaleY);
        EPixelInternalFormat format = rgba8 ? EPixelInternalFormat.Rgba8 : EPixelInternalFormat.Rgba16f;
        EPixelType type = rgba8 ? EPixelType.UnsignedByte : EPixelType.HalfFloat;
        ESizedInternalFormat sized = rgba8 ? ESizedInternalFormat.Rgba8 : ESizedInternalFormat.Rgba16f;
        builder.Texture(name).Size(size).Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(format, EPixelFormat.Rgba, type).SizedFormat(sized)
            .Mips(new RenderResourceMipPolicy(0, mips, AutoGenerateMipmaps: false, RequireImmutableStorage: true))
            .Factory(() =>
            {
                XRTexture texture = CreateLatePostColor(name, width, height, 1, mips, format, EPixelFormat.Rgba, type, sized);
                texture.RequiresStorageUsage = false;
                return texture;
            }).Add();
    }
}
