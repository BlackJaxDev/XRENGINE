using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Logical description of a render pass, independent from any API-specific encoding.
/// </summary>
public sealed class RenderPassMetadata
{
    private readonly List<RenderPassResourceUsage> _resourceUsages = new();
    private readonly HashSet<int> _explicitDependencies = new();
    private readonly HashSet<string> _descriptorSchemas = new(StringComparer.Ordinal);

    public int PassIndex { get; }
    public ERenderGraphPassStage Stage { get; private set; }
    public string Name { get; private set; }

    internal RenderPassMetadata(int passIndex, string name, ERenderGraphPassStage stage)
    {
        PassIndex = passIndex;
        Name = string.IsNullOrWhiteSpace(name) ? $"Pass{passIndex}" : name;
        Stage = stage;
        AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);
        if (stage == ERenderGraphPassStage.Graphics)
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

    internal void UpdateStage(ERenderGraphPassStage stage)
        => Stage = stage;

    internal void UpdateName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            Name = name;
    }
}