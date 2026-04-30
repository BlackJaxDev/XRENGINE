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

        return new TextureResourceDescriptor(
            texture.Name ?? string.Empty,
            lifetime,
            RenderResourceSizePolicy.Absolute(width, height),
            ResolveFormat(texture),
            TryGetStereoFlag(texture),
            depth,
            SupportsAliasing: lifetime == RenderResourceLifetime.Transient,
            RequiresStorageUsage: texture.RequiresStorageUsage);
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

    private static string ResolveFormat(XRTexture texture)
        => texture switch
        {
            XRTexture2D tex2D => tex2D.SizedInternalFormat.ToString(),
            XRTexture2DArray texArray => texArray.SizedInternalFormat.ToString(),
            XRTexture3D tex3D => tex3D.SizedInternalFormat.ToString(),
            XRTextureCube texCube => texCube.SizedInternalFormat.ToString(),
            _ => texture.GetType().Name
        };

    private static bool TryGetStereoFlag(XRTexture texture)
        => texture is XRTexture2DArray texArray && texArray.OVRMultiViewParameters is { NumViews: > 1 };
}
