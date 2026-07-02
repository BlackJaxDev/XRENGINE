namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasPageResource
{
    internal ShadowAtlasPageResource(
        ShadowAtlasPageDescriptor descriptor,
        XRTexture2DArray? texture,
        XRTexture2DArray rasterDepthTexture)
    {
        Descriptor = descriptor;
        Texture = texture;
        RasterDepthTexture = rasterDepthTexture;
        FrameBuffer = Texture is null
            ? new XRFrameBuffer((RasterDepthTexture, EFrameBufferAttachment.DepthAttachment, 0, descriptor.PageIndex))
            : new XRFrameBuffer(
                (Texture, EFrameBufferAttachment.ColorAttachment0, 0, descriptor.PageIndex),
                (RasterDepthTexture, EFrameBufferAttachment.DepthAttachment, 0, descriptor.PageIndex));
    }

    public ShadowAtlasPageDescriptor Descriptor { get; }
    public XRTexture2DArray? Texture { get; }
    public XRTexture2DArray RasterDepthTexture { get; }
    public XRFrameBuffer FrameBuffer { get; }
}
