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

    public RenderPassBuilder WithStage(ERenderGraphPassStage stage)
    {
        _metadata.UpdateStage(stage);
        return this;
    }

    public RenderPassBuilder WithName(string name)
    {
        _metadata.UpdateName(name);
        return this;
    }

    public RenderPassBuilder DependsOn(int passIndex)
    {
        _metadata.AddDependency(passIndex);
        return this;
    }

    public RenderPassBuilder UseDescriptorSchema(string schemaName)
    {
        _metadata.AddDescriptorSchema(schemaName);
        return this;
    }

    public RenderPassBuilder UseEngineDescriptors()
        => UseDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);

    public RenderPassBuilder UseMaterialDescriptors()
        => UseDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);

    public RenderPassBuilder UseColorAttachment(string resourceName, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.ColorAttachment, access, load, store);

    public RenderPassBuilder UseDepthAttachment(string resourceName, ERenderGraphAccess access = ERenderGraphAccess.ReadWrite, ERenderPassLoadOp load = ERenderPassLoadOp.Load, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.DepthAttachment, access, load, store);

    public RenderPassBuilder UseResolveAttachment(string resourceName, ERenderPassStoreOp store = ERenderPassStoreOp.Store)
        => AddUsage(resourceName, ERenderPassResourceType.ResolveAttachment, ERenderGraphAccess.Write, ERenderPassLoadOp.DontCare, store);

    public RenderPassBuilder SampleTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.SampledTexture, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    public RenderPassBuilder ReadBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    public RenderPassBuilder WriteBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.Write, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    public RenderPassBuilder ReadWriteBuffer(string bufferName, ERenderPassResourceType bufferType = ERenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    public RenderPassBuilder SampleStorageTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.StorageTexture, ERenderGraphAccess.Read, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    public RenderPassBuilder ReadWriteTexture(string resourceName)
        => AddUsage(resourceName, ERenderPassResourceType.StorageTexture, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);

    private RenderPassBuilder AddUsage(string resourceName, ERenderPassResourceType type, ERenderGraphAccess access, ERenderPassLoadOp load, ERenderPassStoreOp store)
    {
        if (!string.IsNullOrWhiteSpace(resourceName))
            _metadata.AddUsage(new RenderPassResourceUsage(resourceName, type, access, load, store));

        return this;
    }
}
