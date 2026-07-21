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
    private readonly ReadOnlyCollection<RenderPassResourceUsage> _resourceUsagesView;
    private ReadOnlyCollection<int>? _explicitDependenciesView;
    private ReadOnlyCollection<string>? _descriptorSchemasView;
    private int _revision;

    public int PassIndex { get; }
    public int DeclarationOrder { get; }
    public ERenderGraphPassStage Stage { get; private set; }
    public string Name { get; private set; }
    /// <summary>
    /// Gets whether pipeline readiness for this pass is required before frame submission.
    /// Passes are required by default; optional deferral must be declared explicitly.
    /// </summary>
    public bool RequiresPipelineReady { get; private set; } = true;
    public int Revision => _revision;

    public RenderPassMetadata(int passIndex, string name, ERenderGraphPassStage stage, int? declarationOrder = null)
    {
        PassIndex = passIndex;
        DeclarationOrder = declarationOrder ?? passIndex;
        Name = string.IsNullOrWhiteSpace(name) ? $"Pass{passIndex}" : name;
        Stage = stage;
        _resourceUsagesView = _resourceUsages.AsReadOnly();
        AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.EngineGlobals.Name);
        if (stage == ERenderGraphPassStage.Graphics)
            AddDescriptorSchema(RenderGraphDescriptorSchemaCatalog.MaterialResources.Name);
    }

    public ReadOnlyCollection<RenderPassResourceUsage> ResourceUsages
        => _resourceUsagesView;

    public ReadOnlyCollection<int> ExplicitDependencies
        => _explicitDependenciesView ??= _explicitDependencies
            .OrderBy(static dependency => dependency)
            .ToList()
            .AsReadOnly();

    public ReadOnlyCollection<string> DescriptorSchemas
        => _descriptorSchemasView ??= _descriptorSchemas
            .OrderBy(static schema => schema, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

    internal void AddUsage(RenderPassResourceUsage usage)
    {
        if (usage is null)
            return;

        _resourceUsages.Add(usage);
        _revision++;
    }

    internal void AddDependency(int passIndex)
    {
        if (passIndex == PassIndex)
            return;

        if (_explicitDependencies.Add(passIndex))
        {
            _explicitDependenciesView = null;
            _revision++;
        }
    }

    internal void AddDescriptorSchema(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            return;

        if (_descriptorSchemas.Add(schemaName))
        {
            _descriptorSchemasView = null;
            _revision++;
        }
    }

    internal void UpdateStage(ERenderGraphPassStage stage)
    {
        if (Stage == stage)
            return;

        Stage = stage;
        _revision++;
    }

    internal void UpdateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(Name, name, StringComparison.Ordinal))
            return;

        Name = name;
        _revision++;
    }

    internal void UpdatePipelineReadiness(bool required)
    {
        if (RequiresPipelineReady == required)
            return;

        RequiresPipelineReady = required;
        _revision++;
    }
}
