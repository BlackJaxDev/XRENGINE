using System.Text;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// The changes that a derived pipeline applies on top of its base: command, resource, and schema patches, and setting
/// overrides. A derived pipeline is an instance of the base type with a list of overlays in
/// <see cref="RenderPipeline.Overlays"/>. A derived <c>.xrs</c> script stores one overlay.
/// </summary>
public sealed class RenderPipelineOverlay
{
    /// <summary>Gets or sets the display name of the layer, such as the file name of the script.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parent script path as the script wrote it, for an overlay from <c>derive "parent.xrs";</c>, or
    /// null for an overlay on a code base.
    /// </summary>
    public string? ParentPath { get; set; }

    /// <summary>Gets or sets the full path of the parent script, or null.</summary>
    public string? ParentFullPath { get; set; }

    /// <summary>Gets or sets the full name of the base type when the overlay was saved.</summary>
    public string BaseTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the structure hash of the base when the overlay was saved, or null when the script has no
    /// <c>// base-hash:</c> line. A different current hash starts a rebase report.
    /// </summary>
    public ulong? BaseStructureHash { get; set; }

    /// <summary>Gets the command patches, in apply order.</summary>
    public List<RenderCommandPatch> CommandPatches { get; set; } = [];

    /// <summary>Gets the resource patches, in apply order.</summary>
    public List<RenderResourcePatch> ResourcePatches { get; set; } = [];

    /// <summary>Gets the schema patches, in apply order.</summary>
    public List<PostProcessSchemaPatch> SchemaPatches { get; set; } = [];

    /// <summary>Gets the setting overrides: setting name to script value text.</summary>
    public Dictionary<string, string> SettingOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets the number of patches of all kinds.</summary>
    public int PatchCount => CommandPatches.Count + ResourcePatches.Count + SchemaPatches.Count;

    /// <summary>Returns a deep copy of this overlay.</summary>
    public RenderPipelineOverlay Clone()
    {
        RenderPipelineOverlay copy = new()
        {
            Name = Name,
            ParentPath = ParentPath,
            ParentFullPath = ParentFullPath,
            BaseTypeName = BaseTypeName,
            BaseStructureHash = BaseStructureHash,
            SettingOverrides = new Dictionary<string, string>(SettingOverrides, StringComparer.Ordinal),
        };

        for (int i = 0; i < CommandPatches.Count; i++)
            copy.CommandPatches.Add(CommandPatches[i].Clone());
        for (int i = 0; i < ResourcePatches.Count; i++)
            copy.ResourcePatches.Add(ResourcePatches[i].Clone());
        for (int i = 0; i < SchemaPatches.Count; i++)
            copy.SchemaPatches.Add(SchemaPatches[i].Clone());
        return copy;
    }

    /// <summary>
    /// Returns a hash of the resource patches. The generation key includes it, so a resource patch edit builds a new
    /// generation.
    /// </summary>
    public ulong ComputeResourceHash()
    {
        if (ResourcePatches.Count == 0)
            return 0UL;

        List<AuthoredRenderResource> resources = new(ResourcePatches.Count);
        StringBuilder names = new();
        for (int i = 0; i < ResourcePatches.Count; i++)
        {
            RenderResourcePatch patch = ResourcePatches[i];
            names.Append((int)patch.Kind).Append(':').Append(patch.Name).Append(';');
            if (patch.Resource is { } resource)
                resources.Add(resource);
        }

        return RenderPipelineStructureHash.Mix(AuthoredRenderResourceFactory.ComputeLayoutHash(resources), names.ToString());
    }
}
