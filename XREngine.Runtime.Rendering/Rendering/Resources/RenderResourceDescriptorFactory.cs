using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

internal static class RenderResourceDescriptorFactory
{
    public static TextureResourceDescriptor FromTexture(XRTexture texture, RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        ArgumentNullException.ThrowIfNull(texture);

        Vector3 dims = texture.WidthHeightDepth;
        uint width = (uint)Math.Max(1, (int)MathF.Round(dims.X));
        uint height = (uint)Math.Max(1, (int)MathF.Round(dims.Y));
        uint depth = (uint)Math.Max(1, (int)MathF.Round(dims.Z));

        ESizedInternalFormat? sizedFormat = ResolveSizedInternalFormat(texture);
        EPixelInternalFormat? internalFormat = ResolveInternalFormat(texture);
        EPixelFormat? pixelFormat = ResolvePixelFormat(texture);
        EPixelType? pixelType = ResolvePixelType(texture);
        uint samples = ResolveSampleCount(texture);
        uint mipLevels = ResolveMipLevelCount(texture);
        uint layers = ResolveLayerCount(texture, depth);
        RenderPipelineResourceKind kind = texture is XRTextureViewBase
            ? RenderPipelineResourceKind.TextureView
            : RenderPipelineResourceKind.Texture;

        return new TextureResourceDescriptor(
            texture.Name ?? string.Empty,
            lifetime,
            RenderResourceSizePolicy.Absolute(width, height),
            ResolveFormatLabel(texture, sizedFormat, internalFormat),
            TryGetStereoFlag(texture),
            layers,
            SupportsAliasing: lifetime == RenderResourceLifetime.Transient,
            RequiresStorageUsage: texture.RequiresStorageUsage,
            Kind: kind,
            Usage: InferUsage(texture),
            InternalFormat: internalFormat,
            PixelFormat: pixelFormat,
            PixelType: pixelType,
            SizedInternalFormat: sizedFormat,
            Samples: samples,
            MipPolicy: new RenderResourceMipPolicy(ResolveBaseMipLevel(texture), mipLevels, texture.AutoGenerateMipmaps),
            SourceTextureName: ResolveSourceTextureName(texture),
            BaseMipLevel: ResolveBaseMipLevel(texture),
            MipLevelCount: mipLevels,
            BaseLayer: ResolveBaseLayer(texture),
            LayerCount: layers,
            DepthStencilAspect: ResolveDepthStencilAspect(texture),
            ArrayTarget: ResolveArrayTarget(texture),
            Multisample: ResolveMultisample(texture));
    }

    public static FrameBufferResourceDescriptor FromFrameBuffer(XRFrameBuffer frameBuffer, RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);

        List<FrameBufferAttachmentDescriptor> attachments = [];
        if (frameBuffer.Targets is not null)
        {
            foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
            {
                string resourceName = target switch
                {
                    XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                    _ => target?.GetType().Name ?? string.Empty
                };
                attachments.Add(new FrameBufferAttachmentDescriptor(resourceName, attachment, mipLevel, layerIndex));
            }
        }

        return new FrameBufferResourceDescriptor(
            frameBuffer.Name ?? string.Empty,
            lifetime,
            RenderResourceSizePolicy.Absolute(Math.Max(frameBuffer.Width, 1u), Math.Max(frameBuffer.Height, 1u)),
            attachments);
    }

    public static BufferResourceDescriptor FromBuffer(XRDataBuffer buffer, RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        string name = buffer.AttributeName;
        if (string.IsNullOrWhiteSpace(name))
            name = buffer.Name ?? string.Empty;

        uint elementStride = buffer.ElementSize;
        uint elementCount = elementStride > 0 ? (uint)(buffer.Length / elementStride) : 0;

        EBufferAccessPattern accessPattern = buffer.Target == EBufferTarget.ShaderStorageBuffer
            ? InferAccessPattern(buffer.Usage)
            : EBufferAccessPattern.ReadWrite;

        return new BufferResourceDescriptor(
            name,
            lifetime,
            Math.Max(buffer.Length, 1u),
            buffer.Target,
            buffer.Usage,
            SupportsAliasing: lifetime == RenderResourceLifetime.Transient,
            ElementStride: elementStride,
            ElementCount: elementCount,
            AccessPattern: accessPattern);
    }

    public static RenderBufferResourceDescriptor FromRenderBuffer(XRRenderBuffer renderBuffer, RenderResourceLifetime lifetime = RenderResourceLifetime.Persistent)
    {
        ArgumentNullException.ThrowIfNull(renderBuffer);

        return new RenderBufferResourceDescriptor(
            renderBuffer.Name ?? string.Empty,
            lifetime,
            RenderResourceSizePolicy.Absolute(Math.Max(renderBuffer.Width, 1u), Math.Max(renderBuffer.Height, 1u)),
            renderBuffer.Type,
            renderBuffer.MultisampleCount,
            renderBuffer.FrameBufferAttachment);
    }

    private static EBufferAccessPattern InferAccessPattern(EBufferUsage usage)
        => usage switch
        {
            EBufferUsage.StaticRead or EBufferUsage.StreamRead or EBufferUsage.DynamicRead
                => EBufferAccessPattern.ReadOnly,
            EBufferUsage.StaticDraw or EBufferUsage.StreamDraw or EBufferUsage.DynamicDraw
                => EBufferAccessPattern.WriteOnly,
            _ => EBufferAccessPattern.ReadWrite
        };

    private static string ResolveFormatLabel(
        XRTexture texture,
        ESizedInternalFormat? sizedFormat,
        EPixelInternalFormat? internalFormat)
        => sizedFormat?.ToString()
            ?? internalFormat?.ToString()
            ?? texture.GetType().Name;

    private static bool TryGetStereoFlag(XRTexture texture)
        => texture is XRTexture2DArray texArray && texArray.OVRMultiViewParameters is { NumViews: > 1 };

    private static RenderPipelineResourceUsage InferUsage(XRTexture texture)
    {
        RenderPipelineResourceUsage usage =
            RenderPipelineResourceUsage.SampledTexture |
            RenderPipelineResourceUsage.TransferSource |
            RenderPipelineResourceUsage.TransferDestination;

        if (texture.RequiresStorageUsage)
            usage |= RenderPipelineResourceUsage.StorageImage;

        if (texture.FrameBufferAttachment is EFrameBufferAttachment attachment)
        {
            usage |= attachment is EFrameBufferAttachment.DepthAttachment
                or EFrameBufferAttachment.DepthStencilAttachment
                or EFrameBufferAttachment.StencilAttachment
                ? RenderPipelineResourceUsage.DepthStencilAttachment
                : RenderPipelineResourceUsage.ColorAttachment;
        }

        return usage;
    }

    private static ESizedInternalFormat? ResolveSizedInternalFormat(XRTexture texture)
        => texture switch
        {
            XRTexture1D tex1D => tex1D.SizedInternalFormat,
            XRTexture1DArray tex1DArray => tex1DArray.SizedInternalFormat,
            XRTexture2D tex2D => tex2D.SizedInternalFormat,
            XRTexture2DArray texArray => texArray.SizedInternalFormat,
            XRTexture3D tex3D => tex3D.SizedInternalFormat,
            XRTextureCube texCube => texCube.SizedInternalFormat,
            XRTextureCubeArray texCubeArray => texCubeArray.SizedInternalFormat,
            XRTextureRectangle texRectangle => texRectangle.SizedInternalFormat,
            XRTextureBuffer texBuffer => texBuffer.SizedInternalFormat,
            XRTextureViewBase viewBase => viewBase.InternalFormat,
            _ => null
        };

    private static EPixelInternalFormat? ResolveInternalFormat(XRTexture texture)
        => texture switch
        {
            XRTexture1D tex1D when tex1D.Mipmaps.Length > 0 => tex1D.Mipmaps[0].InternalFormat,
            XRTexture1DArray tex1DArray when tex1DArray.Textures.Length > 0 && tex1DArray.Textures[0].Mipmaps.Length > 0 => tex1DArray.Textures[0].Mipmaps[0].InternalFormat,
            XRTexture2D tex2D when tex2D.Mipmaps.Length > 0 => tex2D.Mipmaps[0].InternalFormat,
            XRTexture2DArray texArray when texArray.Mipmaps is { Length: > 0 } mipmaps => mipmaps[0].InternalFormat,
            XRTexture3D tex3D when tex3D.Mipmaps.Length > 0 => tex3D.Mipmaps[0].InternalFormat,
            XRTextureViewBase viewBase => ResolveInternalFormat(viewBase.GetViewedTexture()),
            _ => null
        };

    private static EPixelFormat? ResolvePixelFormat(XRTexture texture)
        => texture switch
        {
            XRTexture1D tex1D when tex1D.Mipmaps.Length > 0 => tex1D.Mipmaps[0].PixelFormat,
            XRTexture1DArray tex1DArray when tex1DArray.Textures.Length > 0 && tex1DArray.Textures[0].Mipmaps.Length > 0 => tex1DArray.Textures[0].Mipmaps[0].PixelFormat,
            XRTexture2D tex2D when tex2D.Mipmaps.Length > 0 => tex2D.Mipmaps[0].PixelFormat,
            XRTexture2DArray texArray when texArray.Mipmaps is { Length: > 0 } mipmaps => mipmaps[0].PixelFormat,
            XRTexture3D tex3D when tex3D.Mipmaps.Length > 0 => tex3D.Mipmaps[0].PixelFormat,
            XRTextureViewBase viewBase => ResolvePixelFormat(viewBase.GetViewedTexture()),
            _ => null
        };

    private static EPixelType? ResolvePixelType(XRTexture texture)
        => texture switch
        {
            XRTexture1D tex1D when tex1D.Mipmaps.Length > 0 => tex1D.Mipmaps[0].PixelType,
            XRTexture1DArray tex1DArray when tex1DArray.Textures.Length > 0 && tex1DArray.Textures[0].Mipmaps.Length > 0 => tex1DArray.Textures[0].Mipmaps[0].PixelType,
            XRTexture2D tex2D when tex2D.Mipmaps.Length > 0 => tex2D.Mipmaps[0].PixelType,
            XRTexture2DArray texArray when texArray.Mipmaps is { Length: > 0 } mipmaps => mipmaps[0].PixelType,
            XRTexture3D tex3D when tex3D.Mipmaps.Length > 0 => tex3D.Mipmaps[0].PixelType,
            XRTextureViewBase viewBase => ResolvePixelType(viewBase.GetViewedTexture()),
            _ => null
        };

    private static uint ResolveMipLevelCount(XRTexture texture)
    {
        if (texture is XRTextureViewBase viewBase)
            return Math.Max(1u, viewBase.NumLevels);

        if (texture.AutoGenerateMipmaps)
            return (uint)Math.Max(1, texture.SmallestMipmapLevel + 1);

        return texture switch
        {
            XRTexture1D tex1D => (uint)Math.Max(1, tex1D.Mipmaps.Length),
            XRTexture1DArray tex1DArray when tex1DArray.Textures.Length > 0 => (uint)Math.Max(1, tex1DArray.Textures[0].Mipmaps.Length),
            XRTexture2D tex2D => (uint)Math.Max(1, tex2D.Mipmaps.Length),
            XRTexture2DArray texArray => (uint)Math.Max(1, texArray.Mipmaps?.Length ?? 1),
            XRTexture3D tex3D => (uint)Math.Max(1, tex3D.Mipmaps.Length),
            XRTextureCube texCube => (uint)Math.Max(1, texCube.Mipmaps.Length),
            XRTextureCubeArray texCubeArray when texCubeArray.Cubes.Length > 0 => (uint)Math.Max(1, texCubeArray.Cubes[0].Mipmaps.Length),
            _ => 1u
        };
    }

    private static uint ResolveLayerCount(XRTexture texture, uint fallbackDepth)
        => texture switch
        {
            XRTextureViewBase viewBase => Math.Max(1u, viewBase.NumLayers),
            XRTexture1DArray tex1DArray => Math.Max(1u, tex1DArray.Depth),
            XRTexture2DArray texArray => Math.Max(1u, texArray.Depth),
            XRTextureCube => 6u,
            XRTextureCubeArray texCubeArray => Math.Max(1u, texCubeArray.LayerCount * 6u),
            _ => Math.Max(1u, fallbackDepth)
        };

    private static uint ResolveSampleCount(XRTexture texture)
        => texture switch
        {
            XRTexture2D tex2D => Math.Max(1u, tex2D.MultiSampleCount),
            XRTexture2DArray texArray when texArray.MultiSample && texArray.Textures.Length > 0 => Math.Max(2u, texArray.Textures[0].MultiSampleCount),
            XRTexture2DView view when view.Multisample => Math.Max(2u, view.ViewedTexture.MultiSampleCount),
            XRTexture2DArrayView view when view.Multisample && view.ViewedTexture.Textures.Length > 0 => Math.Max(2u, view.ViewedTexture.Textures[0].MultiSampleCount),
            XRTextureViewBase viewBase => ResolveSampleCount(viewBase.GetViewedTexture()),
            _ => 1u
        };

    private static string? ResolveSourceTextureName(XRTexture texture)
        => texture is XRTextureViewBase viewBase
            ? viewBase.GetViewedTexture().Name ?? viewBase.GetViewedTexture().GetDescribingName()
            : null;

    private static uint ResolveBaseMipLevel(XRTexture texture)
        => texture is XRTextureViewBase viewBase ? viewBase.MinLevel : 0u;

    private static uint ResolveBaseLayer(XRTexture texture)
        => texture is XRTextureViewBase viewBase ? viewBase.MinLayer : 0u;

    private static EDepthStencilFmt ResolveDepthStencilAspect(XRTexture texture)
        => texture switch
        {
            XRTexture2DView view => view.DepthStencilViewFormat,
            XRTexture2DArrayView view => view.DepthStencilViewFormat,
            _ => EDepthStencilFmt.None
        };

    private static bool ResolveArrayTarget(XRTexture texture)
        => texture switch
        {
            XRTexture2DView view => view.Array,
            XRTexture2DArrayView view => view.Array,
            _ => false
        };

    private static bool ResolveMultisample(XRTexture texture)
        => texture switch
        {
            XRTexture2DView view => view.Multisample,
            XRTexture2DArrayView view => view.Multisample,
            XRTexture2D tex2D => tex2D.MultiSample,
            XRTexture2DArray texArray => texArray.MultiSample,
            _ => false
        };
}
