using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace XREngine.Rendering;

public partial class DefaultRenderPipeline
{
    internal void LogAttachmentTextureRebuild(XRRenderPipelineInstance instance, string textureName, XRTexture? cachedTexture, string reason)
    {
        /*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Rebuilding attachment texture '{1}' for {2}. Reason={3}. Cached={4}",
            DebugName,
            textureName,
            instance.DebugDescriptor,
            reason,
            cachedTexture is null ? "<none>" : DescribeTexture(cachedTexture));
        */
    }

    internal void LogTextureBinding(XRRenderPipelineInstance instance, string name, XRTexture texture, XRTexture? replacedTexture)
    {
        if (replacedTexture is not null && !ReferenceEquals(replacedTexture, texture))
        {
            /*
            Debug.Rendering(
                "[RenderResourceDiag][{0}] Recreated texture '{1}' for {2}. Destroying previous instance {3}. New={4}",
                DebugName,
                name,
                instance.DebugDescriptor,
                DescribeTexture(replacedTexture),
                DescribeTexture(texture));
            */
            return;
        }

        /*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Created texture '{1}' for {2}. {3}",
            DebugName,
            name,
            instance.DebugDescriptor,
            DescribeTexture(texture));
        */
    }

    internal void LogFrameBufferBinding(XRRenderPipelineInstance instance, string name, XRFrameBuffer frameBuffer, XRFrameBuffer? replacedFrameBuffer)
    {
        if (replacedFrameBuffer is not null && !ReferenceEquals(replacedFrameBuffer, frameBuffer))
        {
            /*
            Debug.Rendering(
                "[RenderResourceDiag][{0}] Recreated FBO '{1}' for {2}. Destroying previous instance {3}. New={4}",
                DebugName,
                name,
                instance.DebugDescriptor,
                DescribeFrameBuffer(replacedFrameBuffer),
                DescribeFrameBuffer(frameBuffer));
            */
            return;
        }

        /*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Created FBO '{1}' for {2}. {3}",
            DebugName,
            name,
            instance.DebugDescriptor,
            DescribeFrameBuffer(frameBuffer));
        */
    }

    internal void LogTextureDestroy(XRRenderPipelineInstance instance, string name, XRTexture texture, string reason)
    {
        /*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Destroying texture '{1}' for {2}. Reason={3}. {4}",
            DebugName,
            name,
            instance.DebugDescriptor,
            reason,
            DescribeTexture(texture));
        */
    }

    internal void LogFrameBufferDestroy(XRRenderPipelineInstance instance, string name, XRFrameBuffer frameBuffer, string reason)
    {
        /*
        Debug.Rendering(
            "[RenderResourceDiag][{0}] Destroying FBO '{1}' for {2}. Reason={3}. {4}",
            DebugName,
            name,
            instance.DebugDescriptor,
            reason,
            DescribeFrameBuffer(frameBuffer));
        */
    }

    private static string DescribeFrameBuffer(XRFrameBuffer frameBuffer)
    {
        StringBuilder builder = new();
        builder.Append(frameBuffer.GetType().Name);
        builder.Append('#').Append(frameBuffer.GetHashCode());
        builder.Append(" name=").Append(string.IsNullOrWhiteSpace(frameBuffer.Name) ? "<unnamed>" : frameBuffer.Name);
        builder.Append(" size=").Append(frameBuffer.Width).Append('x').Append(frameBuffer.Height);
        builder.Append(" samples=").Append(frameBuffer.EffectiveSampleCount);
        builder.Append(" complete=").Append(frameBuffer.IsLastCheckComplete);

        if (frameBuffer.Targets is { Length: > 0 } targets)
        {
            List<string> attachments = [];
            foreach (var (target, attachment, mipLevel, layerIndex) in targets)
            {
                StringBuilder attachmentBuilder = new();
                attachmentBuilder.Append(attachment).Append('=').Append(DescribeAttachment(target));
                if (mipLevel != 0 || layerIndex != -1)
                {
                    attachmentBuilder.Append(" (mip=").Append(mipLevel);
                    if (layerIndex != -1)
                        attachmentBuilder.Append(", layer=").Append(layerIndex);
                    attachmentBuilder.Append(')');
                }

                attachments.Add(attachmentBuilder.ToString());
            }

            builder.Append(" attachments=[").Append(string.Join("; ", attachments)).Append(']');
        }

        return builder.ToString();
    }

    private static string DescribeAttachment(IFrameBufferAttachement attachment)
        => attachment switch
        {
            XRTexture texture => DescribeTexture(texture),
            XRRenderBuffer renderBuffer => DescribeRenderBuffer(renderBuffer),
            _ => $"{attachment.GetType().Name}#{attachment.GetHashCode()} size={attachment.Width}x{attachment.Height}"
        };

    private static string DescribeRenderBuffer(XRRenderBuffer renderBuffer)
    {
        StringBuilder builder = new();
        builder.Append(renderBuffer.GetType().Name);
        builder.Append('#').Append(renderBuffer.GetHashCode());
        builder.Append(" name=").Append(string.IsNullOrWhiteSpace(renderBuffer.Name) ? "<unnamed>" : renderBuffer.Name);
        builder.Append(" size=").Append(renderBuffer.Width).Append('x').Append(renderBuffer.Height);
        builder.Append(" storage=").Append(renderBuffer.Type);
        builder.Append(" samples=").Append(renderBuffer.MultisampleCount);
        builder.Append(" attachment=").Append(renderBuffer.FrameBufferAttachment?.ToString() ?? "<none>");
        return builder.ToString();
    }

    private static string DescribeTexture(XRTexture texture)
    {
        Vector3 size = texture.WidthHeightDepth;
        uint width = (uint)size.X;
        uint height = (uint)size.Y;
        uint depth = (uint)size.Z;

        StringBuilder builder = new();
        builder.Append(texture.GetType().Name);
        builder.Append('#').Append(texture.GetHashCode());
        builder.Append(" name=").Append(string.IsNullOrWhiteSpace(texture.Name) ? "<unnamed>" : texture.Name);
        builder.Append(" size=").Append(width).Append('x').Append(height);
        if (depth > 1u)
            builder.Append('x').Append(depth);

        builder.Append(" attachment=").Append(texture.FrameBufferAttachment?.ToString() ?? "<none>");
        builder.Append(" resizable=").Append(texture.IsResizeable);

        switch (texture)
        {
            case XRTexture2D texture2D:
                builder.Append(" sizedFormat=").Append(texture2D.SizedInternalFormat);
                builder.Append(" mipLevels=").Append(texture2D.Mipmaps?.Length ?? 0);
                builder.Append(" samples=").Append(texture2D.MultiSampleCount > 0u ? texture2D.MultiSampleCount : 1u);
                break;
            case XRTexture2DArray texture2DArray:
                builder.Append(" sizedFormat=").Append(texture2DArray.SizedInternalFormat);
                builder.Append(" layers=").Append(texture2DArray.Depth);
                builder.Append(" mipLevels=").Append(texture2DArray.Mipmaps?.Length ?? 0);
                builder.Append(" multisample=").Append(texture2DArray.MultiSample);
                if (texture2DArray.Textures.Length > 0)
                    builder.Append(" samples=").Append(texture2DArray.Textures[0].MultiSampleCount > 0u ? texture2DArray.Textures[0].MultiSampleCount : 1u);
                break;
            case XRTexture2DView texture2DView:
                builder.Append(" viewed=").Append(DescribeViewedTexture(texture2DView.ViewedTexture));
                builder.Append(" multisample=").Append(texture2DView.Multisample);
                builder.Append(" depthStencilView=").Append(texture2DView.DepthStencilViewFormat);
                break;
            case XRTexture2DArrayView texture2DArrayView:
                builder.Append(" viewed=").Append(DescribeViewedTexture(texture2DArrayView.ViewedTexture));
                builder.Append(" multisample=").Append(texture2DArrayView.Multisample);
                builder.Append(" depthStencilView=").Append(texture2DArrayView.DepthStencilViewFormat);
                break;
        }

        return builder.ToString();
    }

    private static string DescribeViewedTexture(XRTexture texture)
        => $"{(string.IsNullOrWhiteSpace(texture.Name) ? texture.GetType().Name : texture.Name)}#{texture.GetHashCode()}";
}