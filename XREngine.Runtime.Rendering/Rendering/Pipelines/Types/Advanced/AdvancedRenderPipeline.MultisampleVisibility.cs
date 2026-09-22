using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Retains each covered surface until native shading has evaluated every sample.
    /// The canonical single-sample visibility images remain the downstream depth,
    /// ambient-occlusion and editor-selection contract.
    /// </summary>
    private void DeclareMultisampleVisibilityResources(RenderPipelineResourceLayoutBuilder builder, uint layers)
    {
        if (builder.Profile.AntiAliasingMode != EAntiAliasingMode.Msaa || builder.Profile.MsaaSampleCount <= 1u)
            return;

        uint samples = builder.Profile.MsaaSampleCount;
        DeclareMultisampleVisibilityTexture(builder, layers, samples,
            AdvancedVisibilityResourceNames.SamplePositionMultisample,
            EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat,
            ESizedInternalFormat.Rg16f, EFrameBufferAttachment.ColorAttachment3);
        DeclareMultisampleVisibilityTexture(builder, layers, samples,
            AdvancedVisibilityResourceNames.IdentityMultisample,
            EPixelInternalFormat.RG32ui, EPixelFormat.RgInteger, EPixelType.UnsignedInt,
            ESizedInternalFormat.Rg32ui, EFrameBufferAttachment.ColorAttachment0);
        DeclareMultisampleVisibilityTexture(builder, layers, samples,
            AdvancedVisibilityResourceNames.MetadataMultisample,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt,
            ESizedInternalFormat.R32ui, EFrameBufferAttachment.ColorAttachment1);
        DeclareMultisampleVisibilityTexture(builder, layers, samples,
            AdvancedVisibilityResourceNames.SelectionMultisample,
            EPixelInternalFormat.R32ui, EPixelFormat.RedInteger, EPixelType.UnsignedInt,
            ESizedInternalFormat.R32ui, EFrameBufferAttachment.ColorAttachment2);
        DeclareMultisampleVisibilityTexture(builder, layers, samples,
            AdvancedVisibilityResourceNames.DepthStencilMultisample,
            EPixelInternalFormat.Depth32fStencil8, EPixelFormat.DepthStencil, EPixelType.Float32UnsignedInt248Rev,
            ESizedInternalFormat.Depth32fStencil8, EFrameBufferAttachment.DepthStencilAttachment);

        builder.FrameBuffer(AdvancedVisibilityResourceNames.FrameBufferMultisample)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(RenderResourceSizePolicy.Internal())
            .Color(0, AdvancedVisibilityResourceNames.IdentityMultisample, layerIndex: -1)
            .Color(1, AdvancedVisibilityResourceNames.MetadataMultisample, layerIndex: -1)
            .Color(2, AdvancedVisibilityResourceNames.SelectionMultisample, layerIndex: -1)
            .Color(3, AdvancedVisibilityResourceNames.SamplePositionMultisample, layerIndex: -1)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencilMultisample, layerIndex: -1)
            .Factory(CreateMultisampleVisibilityFrameBuffer)
            .Add();
    }

    private void DeclareMultisampleVisibilityTexture(RenderPipelineResourceLayoutBuilder builder, uint layers, uint samples,
        string name, EPixelInternalFormat format, EPixelFormat pixelFormat, EPixelType pixelType,
        ESizedInternalFormat sizedFormat, EFrameBufferAttachment attachment)
        => builder.Texture(name)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(RenderResourceSizePolicy.Internal())
            .Layers(layers)
            .Samples(samples)
            .ArrayTarget()
            .Multisample()
            .StereoCompatible(layers > 1u)
            .Format(format, pixelFormat, pixelType)
            .SizedFormat(sizedFormat)
            .Usage(RenderPipelineResourceUsage.SampledTexture |
                (attachment == EFrameBufferAttachment.DepthStencilAttachment
                    ? RenderPipelineResourceUsage.DepthStencilAttachment
                    : RenderPipelineResourceUsage.ColorAttachment))
            .Factory(() => CreateMultisampleVisibilityTexture(name, layers, samples, format, pixelFormat, pixelType, sizedFormat, attachment))
            .Add();

    private XRTexture CreateMultisampleVisibilityTexture(string name, uint layers, uint samples,
        EPixelInternalFormat format, EPixelFormat pixelFormat, EPixelType pixelType,
        ESizedInternalFormat sizedFormat, EFrameBufferAttachment attachment)
    {
        // An array even for mono gives both backends one sampler2DMSArray ABI.
        XRTexture2DArray texture = XRTexture2DArray.CreateFrameBufferTexture(
            layers, InternalWidth, InternalHeight, format, pixelFormat, pixelType, attachment);
        for (int layer = 0; layer < texture.Textures.Length; layer++)
        {
            texture.Textures[layer].MultiSampleCount = samples;
            texture.Textures[layer].FixedSampleLocations = true;
        }
        texture.MultiSample = true;
        texture.SizedInternalFormat = sizedFormat;
        texture.Resizable = false;
        texture.Name = name;
        texture.SamplerName = name;
        return texture;
    }

    private XRFrameBuffer CreateMultisampleVisibilityFrameBuffer()
        => new(
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.IdentityMultisample), EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.MetadataMultisample), EFrameBufferAttachment.ColorAttachment1, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.SelectionMultisample), EFrameBufferAttachment.ColorAttachment2, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.SamplePositionMultisample), EFrameBufferAttachment.ColorAttachment3, 0, -1),
            (RequireVisibilityAttachment(AdvancedVisibilityResourceNames.DepthStencilMultisample), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = AdvancedVisibilityResourceNames.FrameBufferMultisample,
        };
}
