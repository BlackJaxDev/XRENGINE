using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>
/// Lowers authored resource declarations into <see cref="RenderPipelineResourceLayoutBuilder"/> calls and creates the
/// GPU objects for them. An authored resource and a code resource with the same fields produce equal specs, so the
/// normal generation path builds both.
/// </summary>
public static class AuthoredRenderResourceFactory
{
    /// <summary>Finds a materialized resource of the generation that the factories run in, by name.</summary>
    /// <param name="name">The resource name.</param>
    /// <returns>The attachable resource, or null when it does not exist.</returns>
    public delegate IFrameBufferAttachement? AttachmentResolver(string name);

    /// <summary>Adds each resource to the builder in order.</summary>
    /// <param name="builder">The layout builder of the pipeline.</param>
    /// <param name="resources">The authored declarations.</param>
    /// <param name="resolver">Finds attachments and view sources when the generation materializes.</param>
    /// <exception cref="ArgumentException">Thrown when a declaration is not valid.</exception>
    public static void Declare(RenderPipelineResourceLayoutBuilder builder, IReadOnlyList<AuthoredRenderResource> resources, AttachmentResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resolver);
        for (int i = 0; i < resources.Count; i++)
            Declare(builder, resources[i], resolver);
    }

    /// <summary>Adds one resource to the builder.</summary>
    /// <exception cref="ArgumentException">Thrown when the declaration is not valid.</exception>
    public static void Declare(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(resource.Name))
            throw new ArgumentException("An authored resource has no name.", nameof(resource));

        // The factories capture a copy, so an edit after the layout builds cannot change what this layout creates.
        AuthoredRenderResource snapshot = resource.Clone();
        switch (snapshot.Kind)
        {
            case EAuthoredRenderResourceKind.Texture:
                DeclareTexture(builder, snapshot);
                break;
            case EAuthoredRenderResourceKind.TextureView:
                DeclareTextureView(builder, snapshot, resolver);
                break;
            case EAuthoredRenderResourceKind.RenderBuffer:
                DeclareRenderBuffer(builder, snapshot);
                break;
            case EAuthoredRenderResourceKind.Buffer:
                DeclareBuffer(builder, snapshot);
                break;
            case EAuthoredRenderResourceKind.FrameBuffer:
                DeclareFrameBuffer(builder, snapshot, resolver);
                break;
            case EAuthoredRenderResourceKind.QuadMaterial:
                DeclareQuadMaterial(builder, snapshot, resolver);
                break;
            default:
                throw new ArgumentException(string.Concat("Authored resource '", snapshot.Name, "' has an unknown kind."), nameof(resource));
        }
    }

    /// <summary>
    /// Computes a stable hash of every layout-affecting field. A pipeline adds it to its generation key, so an edit to
    /// an authored declaration creates a new generation through the normal path.
    /// </summary>
    public static ulong ComputeLayoutHash(IReadOnlyList<AuthoredRenderResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        // No authored resources keep the generation key of a pipeline that declares none.
        if (resources.Count == 0)
            return 0UL;

        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < resources.Count; i++)
        {
            AuthoredRenderResource r = resources[i];
            hash = Mix(hash, r.Name);
            hash = Mix(hash, (ulong)r.Kind);
            hash = Mix(hash, (ulong)r.Lifetime);
            hash = Mix(hash, (ulong)r.History);
            hash = Mix(hash, (ulong)r.Size.SizeClass);
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(r.Size.ScaleX));
            hash = Mix(hash, BitConverter.SingleToUInt32Bits(r.Size.ScaleY));
            hash = Mix(hash, r.Size.Width);
            hash = Mix(hash, r.Size.Height);
            hash = Mix(hash, r.Size.RoundUpDivisor);
            hash = Mix(hash, (ulong)r.Format);
            hash = Mix(hash, r.Samples);
            hash = Mix(hash, r.Layers);
            hash = Mix(hash, r.MipLevels);
            hash = Mix(hash, r.LinearFilter ? 1UL : 0UL);
            hash = Mix(hash, r.StorageImage ? 1UL : 0UL);
            hash = Mix(hash, r.Optional ? 1UL : 0UL);
            hash = Mix(hash, r.SourceTexture);
            hash = Mix(hash, r.BaseMipLevel);
            hash = Mix(hash, r.BaseLayer);
            hash = Mix(hash, r.LayerCount);
            hash = Mix(hash, (ulong)r.DepthStencilAspect);
            hash = Mix(hash, (ulong)r.RenderBufferStorage);
            hash = Mix(hash, r.ElementStride);
            hash = Mix(hash, r.ElementCount);
            hash = Mix(hash, (ulong)r.BufferTarget);
            for (int a = 0; a < r.Attachments.Count; a++)
            {
                AuthoredAttachment attachment = r.Attachments[a];
                hash = Mix(hash, attachment.ResourceName);
                hash = Mix(hash, (ulong)attachment.Attachment);
                hash = Mix(hash, (ulong)(uint)attachment.MipLevel);
                hash = Mix(hash, (ulong)(uint)attachment.LayerIndex);
            }

            hash = Mix(hash, r.FragmentShader);
            for (int t = 0; t < r.InputTextures.Count; t++)
                hash = Mix(hash, r.InputTextures[t]);
        }

        return hash;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        // FNV-1a over the eight bytes of the value keeps the hash the same across processes.
        for (int i = 0; i < 8; i++)
        {
            hash ^= (value >> (i * 8)) & 0xFFUL;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong hash, string? value)
    {
        value ??= string.Empty;
        for (int i = 0; i < value.Length; i++)
            hash = Mix(hash, value[i]);

        return Mix(hash, (ulong)value.Length);
    }

    /// <summary>Returns the layer count that a texture uses for a profile.</summary>
    public static uint ResolveLayers(AuthoredRenderResource resource, RenderPipelineResourceProfile profile)
        => resource.Layers > 0u ? resource.Layers : profile.Stereo ? 2u : 1u;

    /// <summary>Returns the usage flags that the layout declares for a texture.</summary>
    public static RenderPipelineResourceUsage GetTextureUsage(PortableRenderFormat format, bool storageImage)
    {
        RenderPipelineResourceUsage usage = RenderPipelineResourceUsage.SampledTexture |
            (format.IsDepth ? RenderPipelineResourceUsage.DepthStencilAttachment : RenderPipelineResourceUsage.ColorAttachment);
        return storageImage ? usage | RenderPipelineResourceUsage.StorageImage : usage;
    }

    /// <summary>Creates the texture of an authored texture declaration.</summary>
    public static XRTexture CreateTexture(AuthoredRenderResource resource, PortableRenderFormat format, uint width, uint height, uint layers)
    {
        width = Math.Max(width, 1u);
        height = Math.Max(height, 1u);
        EFrameBufferAttachment attachment = format.IsDepth
            ? format.HasStencil ? EFrameBufferAttachment.DepthStencilAttachment : EFrameBufferAttachment.DepthAttachment
            : EFrameBufferAttachment.ColorAttachment0;
        ETexMinFilter minFilter = resource.LinearFilter && !format.IsDepth ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
        ETexMagFilter magFilter = resource.LinearFilter && !format.IsDepth ? ETexMagFilter.Linear : ETexMagFilter.Nearest;

        if (layers > 1u)
        {
            XRTexture2DArray array = XRTexture2DArray.CreateFrameBufferTexture(layers, width, height, format.Internal, format.PixelFormat, format.PixelType, attachment);
            array.Name = resource.Name;
            array.SamplerName = resource.Name;
            array.SizedInternalFormat = format.Sized;
            array.Resizable = false;
            array.MinFilter = minFilter;
            array.MagFilter = magFilter;
            return array;
        }

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height, format.Internal, format.PixelFormat, format.PixelType, attachment);
        texture.Name = resource.Name;
        texture.SamplerName = resource.Name;
        texture.SizedInternalFormat = format.Sized;
        texture.MultiSampleCount = Math.Max(resource.Samples, 1u);
        texture.Resizable = false;
        texture.MinFilter = minFilter;
        texture.MagFilter = magFilter;
        return texture;
    }

    /// <summary>Creates the framebuffer of an authored framebuffer declaration.</summary>
    /// <exception cref="InvalidOperationException">Thrown when an attachment does not exist in the generation.</exception>
    public static XRFrameBuffer CreateFrameBuffer(AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        var targets = new (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[resource.Attachments.Count];
        for (int i = 0; i < targets.Length; i++)
        {
            AuthoredAttachment attachment = resource.Attachments[i];
            IFrameBufferAttachement target = resolver(attachment.ResourceName)
                ?? throw new InvalidOperationException(string.Concat("Framebuffer '", resource.Name, "' attaches '", attachment.ResourceName, "', which the generation does not have."));
            targets[i] = (target, attachment.Attachment, attachment.MipLevel, attachment.LayerIndex);
        }

        return new XRFrameBuffer(targets) { Name = resource.Name };
    }

    private static PortableRenderFormat RequireFormat(AuthoredRenderResource resource)
        => PortableRenderFormats.TryGet(resource.Format, out PortableRenderFormat? format)
            ? format!
            : throw new ArgumentException(string.Concat("Authored resource '", resource.Name, "' uses format ", resource.Format.ToString(), ", which is not portable to OpenGL and Vulkan."));

    private static void DeclareTexture(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource)
    {
        PortableRenderFormat format = RequireFormat(resource);
        uint layers = ResolveLayers(resource, builder.Profile);
        uint samples = Math.Max(resource.Samples, 1u);
        if (samples > 1u && layers > 1u)
            throw new ArgumentException(string.Concat("Authored texture '", resource.Name, "' cannot be multisampled and layered."));

        RenderResourceSizePolicy size = resource.Size.ToPolicy();
        (uint width, uint height) = size.Resolve(builder.Profile);
        RenderPipelineResourceLayoutBuilder.TextureSpecBuilder texture = builder.Texture(resource.Name)
            .Lifetime(resource.Lifetime)
            .Size(size)
            .Usage(GetTextureUsage(format, resource.StorageImage))
            .History(resource.History)
            .Format(format.Internal, format.PixelFormat, format.PixelType)
            .SizedFormat(format.Sized)
            .Samples(samples)
            .Layers(layers)
            .StereoCompatible(resource.Layers == 0u)
            .ArrayTarget(layers > 1u)
            .Multisample(samples > 1u)
            .RequiresStorageUsage(resource.StorageImage)
            .Factory(() => CreateTexture(resource, format, width, height, layers));
        if (resource.MipLevels > 1u)
            texture.Mips(new RenderResourceMipPolicy(0u, resource.MipLevels));
        if (resource.Optional)
            texture.Optional();
        texture.Add();
    }

    private static void DeclareTextureView(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(resource.SourceTexture))
            throw new ArgumentException(string.Concat("Authored texture view '", resource.Name, "' has no source texture."));

        PortableRenderFormat format = RequireFormat(resource);
        uint layerCount = resource.LayerCount;
        bool array = layerCount != 1u;
        RenderPipelineResourceLayoutBuilder.TextureViewSpecBuilder view = builder.TextureView(resource.Name, resource.SourceTexture)
            .Lifetime(resource.Lifetime)
            .Size(resource.Size.ToPolicy())
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(format.Sized)
            .MipRange(resource.BaseMipLevel, Math.Max(resource.MipLevels, 1u))
            .Factory(() => CreateTextureView(resource, format, resolver));
        if (layerCount > 0u)
            view.LayerRange(resource.BaseLayer, layerCount);
        if (resource.DepthStencilAspect != EDepthStencilFmt.None)
            view.DepthStencilAspect(resource.DepthStencilAspect);
        view.Target(array, multisample: false);
        if (resource.Optional)
            view.Optional();
        view.Add();
    }

    private static XRTexture CreateTextureView(AuthoredRenderResource resource, PortableRenderFormat format, AttachmentResolver resolver)
    {
        IFrameBufferAttachement? source = resolver(resource.SourceTexture);
        uint mipCount = Math.Max(resource.MipLevels, 1u);
        switch (source)
        {
            case XRTexture2DArray sourceArray:
            {
                uint layerCount = resource.LayerCount > 0u ? resource.LayerCount : Math.Max(sourceArray.Depth - resource.BaseLayer, 1u);
                return new XRTexture2DArrayView(sourceArray, resource.BaseMipLevel, mipCount, resource.BaseLayer, layerCount, format.Sized, layerCount != 1u, false)
                {
                    DepthStencilViewFormat = resource.DepthStencilAspect,
                    Name = resource.Name,
                    SamplerName = resource.Name,
                };
            }
            case XRTexture2D source2D:
                return new XRTexture2DView(source2D, resource.BaseMipLevel, mipCount, format.Sized, false, source2D.MultiSampleCount > 1u)
                {
                    DepthStencilViewFormat = resource.DepthStencilAspect,
                    Name = resource.Name,
                    SamplerName = resource.Name,
                };
            default:
                throw new InvalidOperationException(string.Concat("Texture view '", resource.Name, "' needs a 2D texture or texture array named '", resource.SourceTexture, "'."));
        }
    }

    private static void DeclareRenderBuffer(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource)
    {
        RenderResourceSizePolicy size = resource.Size.ToPolicy();
        (uint width, uint height) = size.Resolve(builder.Profile);
        uint samples = Math.Max(resource.Samples, 1u);
        ERenderBufferStorage storage = resource.RenderBufferStorage;
        RenderPipelineResourceLayoutBuilder.RenderBufferSpecBuilder renderBuffer = builder.RenderBuffer(resource.Name)
            .Lifetime(resource.Lifetime)
            .Size(size)
            .Storage(storage)
            .Samples(samples)
            .Factory(() => new XRRenderBuffer(Math.Max(width, 1u), Math.Max(height, 1u), storage, samples) { Name = resource.Name });
        if (resource.Optional)
            renderBuffer.Optional();
        renderBuffer.Add();
    }

    private static void DeclareBuffer(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource)
    {
        uint stride = Math.Max(resource.ElementStride, 1u);
        uint count = Math.Max(resource.ElementCount, 1u);
        EBufferTarget target = resource.BufferTarget;
        RenderPipelineResourceUsage usage = target == EBufferTarget.UniformBuffer
            ? RenderPipelineResourceUsage.UniformBuffer
            : RenderPipelineResourceUsage.StorageBuffer;
        RenderPipelineResourceLayoutBuilder.BufferSpecBuilder buffer = builder.Buffer(resource.Name)
            .Lifetime(resource.Lifetime)
            .Usage(usage)
            .BufferFormat((ulong)stride * count, target, EBufferUsage.DynamicCopy)
            .Elements(stride, count)
            .Access(EBufferAccessPattern.ReadWrite)
            .Factory(() => new XRDataBuffer(resource.Name, target, count, EComponentType.Struct, stride, false, false, allocateClientSideSource: false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
                GpuProduced = true,
            });
        if (resource.Optional)
            buffer.Optional();
        buffer.Add();
    }

    private static void DeclareFrameBuffer(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        if (resource.Attachments.Count == 0)
            throw new ArgumentException(string.Concat("Authored framebuffer '", resource.Name, "' has no attachments."));

        bool depthOnly = true;
        RenderPipelineResourceLayoutBuilder.FrameBufferSpecBuilder frameBuffer = builder.FrameBuffer(resource.Name)
            .Lifetime(resource.Lifetime)
            .Size(resource.Size.ToPolicy());
        for (int i = 0; i < resource.Attachments.Count; i++)
        {
            AuthoredAttachment attachment = resource.Attachments[i];
            frameBuffer.Attachment(attachment.ResourceName, attachment.Attachment, attachment.MipLevel, attachment.LayerIndex);
            if (attachment.Attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment.Attachment <= EFrameBufferAttachment.ColorAttachment31)
                depthOnly = false;
        }

        frameBuffer
            .Usage(depthOnly ? RenderPipelineResourceUsage.DepthStencilAttachment : RenderPipelineResourceUsage.ColorAttachment)
            .Factory(() => CreateFrameBuffer(resource, resolver));
        if (resource.Optional)
            frameBuffer.Optional();
        frameBuffer.Add();
    }

    /// <summary>Creates the quad framebuffer of an authored quad material declaration.</summary>
    /// <exception cref="InvalidOperationException">Thrown when an input texture does not exist in the generation.</exception>
    public static XRFrameBuffer CreateQuadMaterial(AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        XRTexture?[] textures = new XRTexture?[resource.InputTextures.Count];
        for (int i = 0; i < textures.Length; i++)
        {
            string name = resource.InputTextures[i];
            textures[i] = resolver(name) as XRTexture
                ?? throw new InvalidOperationException(string.Concat("Quad material '", resource.Name, "' samples '", name, "', which the generation does not have as a texture."));
        }

        XRMaterial material = new(textures, XRShader.EngineShader(resource.FragmentShader, EShaderType.Fragment))
        {
            Name = resource.Name,
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
            },
        };

        return new XRQuadFrameBuffer(material, deriveRenderTargetsFromMaterial: false) { Name = resource.Name };
    }

    private static void DeclareQuadMaterial(RenderPipelineResourceLayoutBuilder builder, AuthoredRenderResource resource, AttachmentResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(resource.FragmentShader))
            throw new ArgumentException(string.Concat("Authored quad material '", resource.Name, "' has no fragment shader."));

        RenderPipelineResourceLayoutBuilder.QuadMaterialSpecBuilder quad = builder.QuadMaterial(resource.Name)
            .Lifetime(resource.Lifetime)
            .DependsOn([.. resource.InputTextures])
            .Factory(() => CreateQuadMaterial(resource, resolver));
        if (resource.Optional)
            quad.Optional();
        quad.Add();
    }
}
