using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace XREngine.Rendering.RenderGraph;

public enum RenderGraphDescriptorSchemaKind
{
    EngineGlobal,
    Material,
    Custom
}

public sealed record RenderGraphDescriptorBinding(
    string Name,
    uint Binding,
    RenderPassResourceType ResourceType,
    RenderGraphAccess Access = RenderGraphAccess.Read);

public sealed class RenderGraphDescriptorSchema
{
    private readonly List<RenderGraphDescriptorBinding> _bindings;

    public RenderGraphDescriptorSchema(
        string name,
        RenderGraphDescriptorSchemaKind kind,
        IEnumerable<RenderGraphDescriptorBinding> bindings)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "UnnamedSchema" : name;
        Kind = kind;
        _bindings = [.. (bindings ?? Array.Empty<RenderGraphDescriptorBinding>())];
    }

    public string Name { get; }
    public RenderGraphDescriptorSchemaKind Kind { get; }
    public ReadOnlyCollection<RenderGraphDescriptorBinding> Bindings => _bindings.AsReadOnly();
}

public static class RenderGraphDescriptorSchemaCatalog
{
    public static readonly RenderGraphDescriptorSchema EngineGlobals = new(
        "EngineGlobals",
        RenderGraphDescriptorSchemaKind.EngineGlobal,
        [
            new RenderGraphDescriptorBinding("EngineUniforms", 0, RenderPassResourceType.UniformBuffer, RenderGraphAccess.Read)
        ]);

    public static readonly RenderGraphDescriptorSchema MaterialResources = new(
        "MaterialResources",
        RenderGraphDescriptorSchemaKind.Material,
        [
            new RenderGraphDescriptorBinding("MaterialUniforms", 0, RenderPassResourceType.UniformBuffer, RenderGraphAccess.Read),
            new RenderGraphDescriptorBinding("MaterialTextures", 1, RenderPassResourceType.SampledTexture, RenderGraphAccess.Read),
            new RenderGraphDescriptorBinding("MaterialStorage", 2, RenderPassResourceType.StorageBuffer, RenderGraphAccess.ReadWrite)
        ]);

    private static readonly Dictionary<string, RenderGraphDescriptorSchema> _schemas = new(StringComparer.Ordinal)
    {
        [EngineGlobals.Name] = EngineGlobals,
        [MaterialResources.Name] = MaterialResources
    };

    public static IReadOnlyCollection<RenderGraphDescriptorSchema> All => _schemas.Values;

    public static bool TryGet(string schemaName, out RenderGraphDescriptorSchema? schema)
        => _schemas.TryGetValue(schemaName, out schema);

    public static RenderGraphDescriptorSchema Register(RenderGraphDescriptorSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas[schema.Name] = schema;
        return schema;
    }
}
