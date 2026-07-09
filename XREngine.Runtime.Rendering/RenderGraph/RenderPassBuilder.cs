namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Helper type exposed to render commands/pipelines so they can describe their pass requirements declaratively.
/// </summary>
public sealed class RenderPassBuilder
{
    private readonly RenderPassMetadata _metadata;

    internal RenderPassBuilder(RenderPassMetadata metadata)
    {
        _metadata = metadata;
    }

    /// <summary>
    /// Gets the index of the render pass being described.
    /// </summary>
    public int PassIndex => _metadata.PassIndex;

    /// <summary>
    /// Sets the stage of the render pass.
    /// </summary>
    /// <param name="stage">The stage to set for the render pass.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder WithStage(ERenderGraphPassStage stage)
    {
        _metadata.UpdateStage(stage);
        return this;
    }

    /// <summary>
    /// Sets the name of the render pass.
    /// </summary>
    /// <param name="name">The name to set for the render pass.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder WithName(string name)
    {
        _metadata.UpdateName(name);
        return this;
    }

    /// <summary>
    /// Adds a dependency on another render pass by its index.
    /// This indicates that the current render pass depends on the completion of the specified pass.
    /// </summary>
    /// <param name="passIndex">The index of the render pass to depend on.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder DependsOn(int passIndex)
    {
        _metadata.AddDependency(passIndex);
        return this;
    }

    /// <summary>
    /// Adds a dependency on another render pass by its name.
    /// This indicates that the current render pass depends on the completion of the specified pass.
    /// </summary>
    /// <param name="schemaName">The name of the render pass to depend on.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseDescriptorSchema(string schemaName)
    {
        _metadata.AddDescriptorSchema(schemaName);
        return this;
    }

    /// <summary>
    /// Configures the render pass to use the engine's global descriptor schema, which provides access to engine-level resources and settings.
    /// </summary>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseEngineDescriptors()
        => UseDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);

    /// <summary>
    /// Configures the render pass to use the material descriptor schema, which provides access to material-related resources and settings.
    /// </summary>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseMaterialDescriptors()
        => UseDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);

    /// <summary>
    /// Configures the render pass to use the camera descriptor schema, which provides access to camera-related resources and settings.
    /// </summary>
    /// <param name="resourceName">The name of the camera resource.</param>
    /// <param name="access">The access type for the resource.</param>
    /// <param name="load">The load operation for the resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseColorAttachment(string resourceName, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.ColorAttachment, access, load, store);

    /// <summary>
    /// Configures the render pass to use a color attachment with a specific mip level, which provides access to a specific level of detail of the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the color attachment resource.</param>
    /// <param name="mipLevel">The mip level of the color attachment.</param>
    /// <param name="access">The access type for the resource.</param>
    /// <param name="load">The load operation for the resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseColorAttachmentMip(string resourceName, uint mipLevel, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.ColorAttachment, access, load, store, SingleMipRange(mipLevel));

    /// <summary>
    /// Configures the render pass to use a depth attachment, which provides access to depth-related resources and settings.
    /// </summary>
    /// <param name="resourceName">The name of the depth attachment resource.</param>
    /// <param name="access">The access type for the resource.</param>
    /// <param name="load">The load operation for the resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseDepthAttachment(string resourceName, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.DepthAttachment, access, load, store);

    /// <summary>
    /// Configures the render pass to use a stencil attachment, which provides access to stencil-related resources and settings.
    /// </summary>
    /// <param name="resourceName">The name of the stencil attachment resource.</param>
    /// <param name="access">The access type for the resource.</param>
    /// <param name="load">The load operation for the resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseStencilAttachment(string resourceName, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.StencilAttachment, access, load, store);

    /// <summary>
    /// Configures the render pass to use a resolve attachment, which provides access to resolve-related resources and settings.
    /// </summary>
    /// <param name="resourceName">The name of the resolve attachment resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseResolveAttachment(string resourceName, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.ResolveAttachment, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, store);

    /// <summary>
    /// Configures the render pass to use a resolve attachment with a specific source color index, which provides access to resolve-related resources and settings.
    /// </summary>
    /// <param name="resourceName">The name of the resolve attachment resource.</param>
    /// <param name="sourceColorIndex">The index of the source color attachment.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseResolveAttachment(string resourceName, uint sourceColorIndex, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(
            resourceName,
            ERenderPassResourceType.ResolveAttachment,
            ERenderGraphAccess.Write,
            ERenderPassLoadOp.DontCare,
            store,
            null,
            sourceColorIndex);

    /// <summary>
    /// Configures the render pass to sample a texture, which provides read access to the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the texture resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder SampleTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to sample a specific mip level of a texture, which provides read access to a specific level of detail of the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the texture resource.</param>
    /// <param name="mipLevel">The mip level to sample.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder SampleTextureMip(string resourceName, uint mipLevel)
        => AddUsage(resourceName, ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store, SingleMipRange(mipLevel));

    /// <summary>
    /// Configures the render pass to sample multiple mip levels of a texture, which provides read access to a range of levels of detail of the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the texture resource.</param>
    /// <param name="baseMipLevel">The base mip level to start sampling from.</param>
    /// <param name="mipLevelCount">The number of mip levels to sample.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder SampleTextureMips(string resourceName, uint baseMipLevel, uint mipLevelCount)
        => AddUsage(
            resourceName,
            ERenderPassResourceType.SampledTexture,
            ERenderGraphAccess.Read,
            ERenderPassLoadOp.Load,
            ERenderPassStoreOp.Store,
            new RenderGraphSubresourceRange(baseMipLevel, mipLevelCount, 0u, RenderGraphSubresourceRange.Remaining));

    /// <summary>
    /// Configures the render pass to read from a buffer, which provides read access to the buffer resource.
    /// </summary>
    /// <param name="bufferName">The name of the buffer resource.</param>
    /// <param name="bufferType">The type of the buffer resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder ReadBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to write to a buffer, which provides write access to the buffer resource.
    /// </summary>
    /// <param name="bufferName">The name of the buffer resource.</param>
    /// <param name="bufferType">The type of the buffer resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder WriteBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.Write, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to read and write to a buffer, which provides read and write access to the buffer resource.
    /// </summary>
    /// <param name="bufferName">The name of the buffer resource.</param>
    /// <param name="bufferType">The type of the buffer resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder ReadWriteBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to sample a storage texture, which provides read access to the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the texture resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder SampleStorageTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to read and write to a texture, which provides read and write access to the texture resource.
    /// </summary>
    /// <param name="resourceName">The name of the texture resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder ReadWriteTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.StorageTexture, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to use a transfer source, which provides read access to the resource for transfer operations.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseTransferSource(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.TransferSource, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to use a transfer destination, which provides write access to the resource for transfer operations.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    public RenderPassBuilder UseTransferDestination(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.TransferDestination, ERenderGraphAccess.Write, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    /// <summary>
    /// Configures the render pass to use a transfer source and destination, which provides read and write access to the resource for transfer operations.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="type">The type of the resource.</param>
    /// <param name="access">The access type for the resource.</param>
    /// <param name="load">The load operation for the resource.</param>
    /// <param name="store">The store operation for the resource.</param>
    /// <param name="subresourceRange">The subresource range for the resource.</param>
    /// <param name="resolveSourceColorIndex">The resolve source color index for the resource.</param>
    /// <returns>The current instance of <see cref="RenderPassBuilder"/> for method chaining.</returns>
    private RenderPassBuilder AddUsage(
        string resourceName,
        ERenderPassResourceType type,
        ERenderGraphAccess access,
        ERenderPassLoadOp load,
        ERenderPassStoreOp store,
        RenderGraphSubresourceRange? subresourceRange = null,
        uint? resolveSourceColorIndex = null)
    {
        if (!string.IsNullOrWhiteSpace(resourceName))
            _metadata.AddUsage(new RenderPassResourceUsage(resourceName, type, access, load, store, subresourceRange, resolveSourceColorIndex));

        return this;
    }

    /// <summary>
    /// Creates a <see cref="RenderGraphSubresourceRange"/> that represents a single mip level.
    /// </summary>
    /// <param name="mipLevel">The mip level.</param>
    /// <returns>A <see cref="RenderGraphSubresourceRange"/> representing the specified mip level.</returns>
    private static RenderGraphSubresourceRange SingleMipRange(uint mipLevel)
        => new(mipLevel, 1u, 0u, RenderGraphSubresourceRange.Remaining);
}
