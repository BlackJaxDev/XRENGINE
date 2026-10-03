using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private void DeclarePackedMultisampleVisibilityResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderPipelineResourceProfile profile = builder.Profile;
        if (profile.MsaaSampleCount != 4 || profile.ViewCount != 1 || profile.Stereo)
            throw new NotSupportedException("Packed visibility requires the exact mono four-sample profile.");
        DeclarePackedMultisampleTexture(builder, AdvancedVisibilityResourceNames.IdentityMultisample,
            EPixelInternalFormat.Rgba16ui, EPixelFormat.RgbaInteger, EPixelType.UnsignedShort, ESizedInternalFormat.Rgba16ui,
            EFrameBufferAttachment.ColorAttachment0);
        DeclarePackedMultisampleTexture(builder, AdvancedVisibilityResourceNames.MetadataSelectionMultisample,
            EPixelInternalFormat.Rgba16ui, EPixelFormat.RgbaInteger, EPixelType.UnsignedShort, ESizedInternalFormat.Rgba16ui,
            EFrameBufferAttachment.ColorAttachment1);
        DeclarePackedMultisampleTexture(builder, AdvancedVisibilityResourceNames.SamplePositionMultisample,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f,
            EFrameBufferAttachment.ColorAttachment2);
        DeclarePackedMultisampleTexture(builder, AdvancedVisibilityResourceNames.DepthStencilMultisample,
            EPixelInternalFormat.Depth32fStencil8, EPixelFormat.DepthStencil, EPixelType.Float32UnsignedInt248Rev,
            ESizedInternalFormat.Depth32fStencil8, EFrameBufferAttachment.DepthStencilAttachment);
        builder.FrameBuffer(AdvancedVisibilityResourceNames.FrameBufferMultisample)
            .Lifetime(RenderResourceLifetime.Persistent).Size(RenderResourceSizePolicy.Internal())
            .Color(0, AdvancedVisibilityResourceNames.IdentityMultisample)
            .Color(1, AdvancedVisibilityResourceNames.MetadataSelectionMultisample)
            .Color(2, AdvancedVisibilityResourceNames.SamplePositionMultisample)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencilMultisample)
            .Factory(() => new XRFrameBuffer(
                (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.IdentityMultisample), EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.MetadataSelectionMultisample), EFrameBufferAttachment.ColorAttachment1, 0, -1),
                (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.SamplePositionMultisample), EFrameBufferAttachment.ColorAttachment2, 0, -1),
                (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencilMultisample), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
                { Name = AdvancedVisibilityResourceNames.FrameBufferMultisample })
            .Add();
        if (UsesMinimalVisibilityOutput) return;
        builder.Texture(AdvancedShadingResourceNames.SampleRadianceReactive)
            .Lifetime(RenderResourceLifetime.Persistent).Size(RenderResourceSizePolicy.Internal())
            .Layers(4).ArrayTarget().Samples(1)
            .Format(EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float)
            .SizedFormat(ESizedInternalFormat.Rgba32f).RequiresStorageUsage(true)
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.StorageImage)
            .Factory(() => CreateWebMultisampleRadiance(profile.InternalWidth, profile.InternalHeight)).Add();
    }

    private static void DeclarePackedMultisampleTexture(RenderPipelineResourceLayoutBuilder builder, string name,
        EPixelInternalFormat format, EPixelFormat pixelFormat, EPixelType pixelType,
        ESizedInternalFormat sizedFormat, EFrameBufferAttachment attachment)
    {
        uint width = builder.Profile.InternalWidth, height = builder.Profile.InternalHeight;
        builder.Texture(name).Lifetime(RenderResourceLifetime.Persistent).Size(RenderResourceSizePolicy.Internal())
            .Samples(4).Multisample().Format(format, pixelFormat, pixelType).SizedFormat(sizedFormat)
            .Usage(RenderPipelineResourceUsage.SampledTexture | (attachment == EFrameBufferAttachment.DepthStencilAttachment
                ? RenderPipelineResourceUsage.DepthStencilAttachment : RenderPipelineResourceUsage.ColorAttachment))
            .Factory(() =>
            {
                XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height, format, pixelFormat, pixelType, attachment);
                texture.MultiSampleCount = 4; texture.FixedSampleLocations = true;
                texture.SizedInternalFormat = sizedFormat; texture.Resizable = false;
                texture.Name = name; texture.SamplerName = name;
                return texture;
            }).Add();
    }

    private static XRTexture2DArray CreateWebMultisampleRadiance(uint width, uint height)
    {
        XRTexture2DArray texture = XRTexture2DArray.CreateFrameBufferTexture(4, width, height,
            EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, EFrameBufferAttachment.ColorAttachment0);
        texture.Name = AdvancedShadingResourceNames.SampleRadianceReactive;
        texture.SamplerName = texture.Name; texture.SizedInternalFormat = ESizedInternalFormat.Rgba32f;
        texture.RequiresStorageUsage = true; texture.Resizable = false;
        texture.MinFilter = ETexMinFilter.Nearest; texture.MagFilter = ETexMagFilter.Nearest;
        return texture;
    }
}
