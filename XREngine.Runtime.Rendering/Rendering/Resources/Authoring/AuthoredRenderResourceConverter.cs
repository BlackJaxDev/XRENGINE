using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>
/// Converts the resource specs of a code layout into authored declarations. Fork and the expanded script export use
/// it to make a standalone pipeline from a code pipeline.
/// </summary>
public static class AuthoredRenderResourceConverter
{
    /// <summary>
    /// Converts the specs of <paramref name="layout"/> that are enabled for its profile. A spec that only a factory can
    /// make adds a line to <paramref name="unconverted"/>.
    /// </summary>
    public static List<AuthoredRenderResource> FromLayout(RenderPipelineResourceLayout layout, List<string> unconverted)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(unconverted);
        List<AuthoredRenderResource> output = [];
        for (int i = 0; i < layout.OrderedSpecs.Count; i++)
        {
            RenderPipelineResourceSpec spec = layout.OrderedSpecs[i];
            if (!spec.IsEnabled(layout.Profile))
                continue;

            AuthoredRenderResource? resource = ConvertSpec(spec);
            if (resource is null)
            {
                unconverted.Add(string.Concat("resource '", spec.Name, "' (", spec.Kind.ToString(), "): only code can create it"));
                continue;
            }

            resource.Name = spec.Name;
            resource.Lifetime = spec.Lifetime;
            resource.History = spec.HistoryPolicy;
            resource.Optional = !spec.Required;
            output.Add(resource);
        }

        return output;
    }

    private static AuthoredRenderResource? ConvertSpec(RenderPipelineResourceSpec spec)
        => spec switch
        {
            TextureSpec { SizedInternalFormat: { } format } texture => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.Texture,
                Size = AuthoredSizePolicy.FromPolicy(texture.SizePolicy),
                Format = format,
                Samples = Math.Max(1u, texture.Samples),
                Layers = texture.Layers,
                MipLevels = Math.Max(1u, texture.MipPolicy.MipLevelCount),
                StorageImage = texture.RequiresStorageUsage,
            },
            TextureViewSpec view => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.TextureView,
                Size = AuthoredSizePolicy.FromPolicy(view.SizePolicy),
                SourceTexture = view.SourceTextureName,
                BaseMipLevel = view.BaseMipLevel,
                MipLevels = Math.Max(1u, view.MipLevelCount),
                BaseLayer = view.BaseLayer,
                LayerCount = view.LayerCount,
                Format = view.SizedInternalFormat ?? ESizedInternalFormat.Rgba16f,
                DepthStencilAspect = view.DepthStencilAspect,
            },
            FrameBufferSpec { Attachments.Count: > 0 } frameBuffer => ConvertFrameBuffer(frameBuffer),
            RenderBufferSpec renderBuffer => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.RenderBuffer,
                Size = AuthoredSizePolicy.FromPolicy(renderBuffer.SizePolicy),
                RenderBufferStorage = renderBuffer.StorageFormat,
                Samples = Math.Max(1u, renderBuffer.Samples),
            },
            BufferSpec buffer when buffer.ElementCount > 0 => new AuthoredRenderResource
            {
                Kind = EAuthoredRenderResourceKind.Buffer,
                ElementStride = buffer.ElementStride,
                ElementCount = buffer.ElementCount,
                BufferTarget = buffer.Target,
            },
            _ => null,
        };

    private static AuthoredRenderResource ConvertFrameBuffer(FrameBufferSpec spec)
    {
        AuthoredRenderResource resource = new() { Kind = EAuthoredRenderResourceKind.FrameBuffer };
        for (int i = 0; i < spec.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor attachment = spec.Attachments[i];
            resource.Attachments.Add(new AuthoredAttachment
            {
                ResourceName = attachment.ResourceName,
                Attachment = attachment.Attachment,
                MipLevel = attachment.MipLevel,
                LayerIndex = attachment.LayerIndex,
            });
        }

        return resource;
    }
}
