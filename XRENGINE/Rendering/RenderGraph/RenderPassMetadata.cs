using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// High-level stage classification for a render pass. Used to reason about which pipeline
/// domains (graphics/compute/transfer) the pass touches before backend compilation.
/// </summary>
public enum RenderGraphPassStage
{
    Graphics,
    Compute,
    Transfer
}

/// <summary>
/// Describes the role a logical resource plays inside a render pass.
/// </summary>
public enum RenderPassResourceType
{
    ColorAttachment,
    DepthAttachment,
    StencilAttachment,
    ResolveAttachment,
    SampledTexture,
    StorageTexture,
    UniformBuffer,
    StorageBuffer,
    VertexBuffer,
    IndexBuffer,
    IndirectBuffer,
    TransferSource,
    TransferDestination
}

/// <summary>
/// Access intent for a render resource.
/// </summary>
public enum RenderGraphAccess
{
    Read,
    Write,
    ReadWrite
}

public enum RenderPassLoadOp
{
    Load,
    Clear,
    DontCare
}

public enum RenderPassStoreOp
{
    Store,
    DontCare
}

/// <summary>
/// Records how a pass touches a named logical resource (texture, buffer, etc.).
/// </summary>
public sealed class RenderPassResourceUsage
{
    public string ResourceName { get; }
    public RenderPassResourceType ResourceType { get; }
    public RenderGraphAccess Access { get; }
    public RenderPassLoadOp LoadOp { get; }
    public RenderPassStoreOp StoreOp { get; }

    public RenderPassResourceUsage(
        string resourceName,
        RenderPassResourceType resourceType,
        RenderGraphAccess access,
        RenderPassLoadOp loadOp = RenderPassLoadOp.Load,
        RenderPassStoreOp storeOp = RenderPassStoreOp.Store)
    {
        ResourceName = resourceName;
        ResourceType = resourceType;
        Access = access;
        LoadOp = loadOp;
        StoreOp = storeOp;
    }

    public bool IsAttachment => ResourceType is RenderPassResourceType.ColorAttachment or RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment or RenderPassResourceType.ResolveAttachment;
}

/// <summary>
/// Logical description of a render pass, independent from any API-specific encoding.
/// </summary>
public sealed class RenderPassMetadata
{
    private readonly List<RenderPassResourceUsage> _resourceUsages = new();
    private readonly HashSet<int> _explicitDependencies = new();
    private readonly HashSet<string> _descriptorSchemas = new(StringComparer.Ordinal);

    public int PassIndex { get; }
    public RenderGraphPassStage Stage { get; private set; }
    public string Name { get; private set; }

    internal RenderPassMetadata(int passIndex, string name, RenderGraphPassStage stage)
    {
        PassIndex = passIndex;
        Name = string.IsNullOrWhiteSpace(name) ? $"Pass{passIndex}" : name;
        Stage = stage;
        AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);
        if (stage == RenderGraphPassStage.Graphics)
            AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);
    }

    public ReadOnlyCollection<RenderPassResourceUsage> ResourceUsages
        => _resourceUsages.AsReadOnly();

    public ReadOnlyCollection<int> ExplicitDependencies
        => _explicitDependencies.ToList().AsReadOnly();

    public ReadOnlyCollection<string> DescriptorSchemas
        => _descriptorSchemas.ToList().AsReadOnly();

    internal void AddUsage(RenderPassResourceUsage usage)
    {
        if (usage is null)
            return;
        _resourceUsages.Add(usage);
    }

    internal void AddDependency(int passIndex)
    {
        if (passIndex == PassIndex)
            return;
        _explicitDependencies.Add(passIndex);
    }

    internal void AddDescriptorSchema(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            return;

        _descriptorSchemas.Add(schemaName);
    }

    internal void UpdateStage(RenderGraphPassStage stage)
        => Stage = stage;

    internal void UpdateName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            Name = name;
    }
}

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

    public RenderPassBuilder WithStage(RenderGraphPassStage stage)
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

    public RenderPassBuilder UseColorAttachment(string resourceName, RenderGraphAccess access = RenderGraphAccess.ReadWrite, RenderPassLoadOp load = RenderPassLoadOp.Load, RenderPassStoreOp store = RenderPassStoreOp.Store)
        => AddUsage(resourceName, RenderPassResourceType.ColorAttachment, access, load, store);

    public RenderPassBuilder UseDepthAttachment(string resourceName, RenderGraphAccess access = RenderGraphAccess.ReadWrite, RenderPassLoadOp load = RenderPassLoadOp.Load, RenderPassStoreOp store = RenderPassStoreOp.Store)
        => AddUsage(resourceName, RenderPassResourceType.DepthAttachment, access, load, store);

    public RenderPassBuilder UseResolveAttachment(string resourceName, RenderPassStoreOp store = RenderPassStoreOp.Store)
        => AddUsage(resourceName, RenderPassResourceType.ResolveAttachment, RenderGraphAccess.Write, RenderPassLoadOp.DontCare, store);

    public RenderPassBuilder SampleTexture(string resourceName)
        => AddUsage(resourceName, RenderPassResourceType.SampledTexture, RenderGraphAccess.Read, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    public RenderPassBuilder ReadBuffer(string bufferName, RenderPassResourceType bufferType = RenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, RenderGraphAccess.Read, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    public RenderPassBuilder WriteBuffer(string bufferName, RenderPassResourceType bufferType = RenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, RenderGraphAccess.Write, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    public RenderPassBuilder ReadWriteBuffer(string bufferName, RenderPassResourceType bufferType = RenderPassResourceType.StorageBuffer)
        => AddUsage(bufferName, bufferType, RenderGraphAccess.ReadWrite, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    public RenderPassBuilder SampleStorageTexture(string resourceName)
        => AddUsage(resourceName, RenderPassResourceType.StorageTexture, RenderGraphAccess.Read, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    public RenderPassBuilder ReadWriteTexture(string resourceName)
        => AddUsage(resourceName, RenderPassResourceType.StorageTexture, RenderGraphAccess.ReadWrite, RenderPassLoadOp.Load, RenderPassStoreOp.Store);

    private RenderPassBuilder AddUsage(string resourceName, RenderPassResourceType type, RenderGraphAccess access, RenderPassLoadOp load, RenderPassStoreOp store)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
            return this;

        _metadata.AddUsage(new RenderPassResourceUsage(resourceName, type, access, load, store));
        return this;
    }
}

/// <summary>
/// Mutable accumulator used while describing passes. Converts to an immutable list once the pipeline is done.
/// </summary>
public sealed class RenderPassMetadataCollection
{
    private readonly Dictionary<int, RenderPassMetadata> _passes = new();

    public RenderPassBuilder ForPass(int passIndex, string? name = null, RenderGraphPassStage stage = RenderGraphPassStage.Graphics)
    {
        if (!_passes.TryGetValue(passIndex, out RenderPassMetadata? metadata))
        {
            metadata = new RenderPassMetadata(passIndex, name ?? $"Pass{passIndex}", stage);
            _passes.Add(passIndex, metadata);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(name))
                metadata.UpdateName(name!);
            metadata.UpdateStage(stage);
        }

        EnsureDefaultDescriptorSchemas(metadata);

        return new RenderPassBuilder(metadata);
    }

    private static void EnsureDefaultDescriptorSchemas(RenderPassMetadata metadata)
    {
        metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);

        if (metadata.Stage == RenderGraphPassStage.Graphics)
            metadata.AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);
    }

    public IReadOnlyCollection<RenderPassMetadata> Build()
        => new ReadOnlyCollection<RenderPassMetadata>(_passes.Values.OrderBy(p => p.PassIndex).ToList());
}
