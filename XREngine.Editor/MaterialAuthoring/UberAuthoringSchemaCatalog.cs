using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Builds the material authoring tree of the Uber shader from its own feature and property annotations.
/// </summary>
/// <remarks>
/// The tree has one section for each category and one subsection for each feature or subcategory. Semantic IDs
/// contain only Uber names, so saved presets and clipboard data do not depend on an imported source shader.
/// </remarks>
public static class UberAuthoringSchemaCatalog
{
    /// <summary>
    /// The ID of the Uber authoring schema.
    /// </summary>
    public const string SchemaId = "uber";

    /// <summary>
    /// The prefix of each semantic ID in the Uber authoring schema.
    /// </summary>
    public const string SemanticIdPrefix = "uber/";

    private const string DefaultCategory = "General";
    private const int SchemaVersion = 1;

    private static readonly ConditionalWeakTable<ShaderUiManifest, ShaderAuthoringSchema> Cache = new();

    /// <summary>
    /// Returns the cached schema of the manifest, and builds it on first use.
    /// </summary>
    public static ShaderAuthoringSchema GetOrCreate(ShaderUiManifest manifest)
        => Cache.GetValue(manifest, Build);

    /// <summary>
    /// Returns the semantic ID of the property node with the given Uber property name.
    /// </summary>
    public static string GetPropertySemanticId(string propertyName)
        => $"{SemanticIdPrefix}property/{propertyName}";

    private static ShaderAuthoringSchema Build(ShaderUiManifest manifest)
    {
        ShaderAuthoringNode root = new()
        {
            SemanticId = $"{SemanticIdPrefix}root",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = "Uber",
            DeclarationOrder = -1,
        };

        // Group the properties first. The manifest can declare the properties of one category in more than
        // one place, and the declaration order of the nodes must follow the tree order.
        // Each section item is a property or the key of a subsection.
        List<(string Name, List<(ShaderUiProperty? Property, string? SubsectionKey)> Items)> sections = [];
        Dictionary<string, int> sectionIndices = new(StringComparer.Ordinal);
        Dictionary<string, (string DisplayName, string? Tooltip, List<ShaderUiProperty> Properties)> subsections =
            new(StringComparer.Ordinal);
        foreach (ShaderUiProperty property in manifest.Properties)
        {
            if (!IsAuthorable(property))
                continue;

            manifest.FeatureLookup.TryGetValue(property.FeatureId ?? string.Empty, out ShaderUiFeature? feature);
            string category = ResolveCategory(property, feature);
            if (!sectionIndices.TryGetValue(category, out int sectionIndex))
            {
                sectionIndex = sections.Count;
                sections.Add((category, []));
                sectionIndices[category] = sectionIndex;
            }

            var items = sections[sectionIndex].Items;
            if (!TryResolveSubsection(property, feature, out string subsectionKey, out string subsectionName, out string? tooltip))
            {
                items.Add((property, null));
                continue;
            }

            string key = $"{category}/{subsectionKey}";
            if (!subsections.TryGetValue(key, out var subsection))
            {
                subsection = (subsectionName, tooltip, []);
                subsections[key] = subsection;
                items.Add((null, key));
            }
            subsection.Properties.Add(property);
        }

        int order = 0;
        foreach ((string sectionName, var items) in sections)
        {
            ShaderAuthoringNode sectionNode = CreateGroup(
                EShaderAuthoringNodeKind.Section, $"section/{sectionName}", sectionName, null, order++);
            AddChild(root, sectionNode);
            foreach ((ShaderUiProperty? property, string? key) in items)
            {
                if (property is not null)
                {
                    AddChild(sectionNode, CreateProperty(property, order++));
                    continue;
                }

                var subsection = subsections[key!];
                ShaderAuthoringNode subsectionNode = CreateGroup(
                    EShaderAuthoringNodeKind.Subsection, $"subsection/{key}", subsection.DisplayName,
                    subsection.Tooltip, order++);
                AddChild(sectionNode, subsectionNode);
                foreach (ShaderUiProperty subsectionProperty in subsection.Properties)
                    AddChild(subsectionNode, CreateProperty(subsectionProperty, order++));
            }
        }

        const string sourceIdentity = "Uber shader";
        return new ShaderAuthoringSchema(
            SchemaId,
            SchemaVersion,
            sourceIdentity,
            ComputeFingerprint(sourceIdentity, manifest),
            root,
            []);
    }

    /// <summary>
    /// Returns true for an annotated material property. Engine-driven uniforms are not authorable.
    /// </summary>
    private static bool IsAuthorable(ShaderUiProperty property)
        => property.HasExplicitMetadata &&
           (property.Name.StartsWith('_') || property.Name.Equals("AlphaCutoff", StringComparison.Ordinal)) &&
           UniformRequirementsDetection.GetAllProviders(property.Name) == EUniformRequirements.None;

    private static string ResolveCategory(ShaderUiProperty property, ShaderUiFeature? feature)
    {
        if (!string.IsNullOrWhiteSpace(property.Category))
            return property.Category;
        if (!string.IsNullOrWhiteSpace(feature?.Category))
            return feature.Category;
        return DefaultCategory;
    }

    private static bool TryResolveSubsection(
        ShaderUiProperty property,
        ShaderUiFeature? feature,
        out string key,
        out string displayName,
        out string? tooltip)
    {
        if (feature is not null)
        {
            key = $"feature/{feature.Id}";
            displayName = feature.DisplayName;
            tooltip = feature.Tooltip;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(property.Subcategory))
        {
            key = property.Subcategory;
            displayName = property.Subcategory;
            tooltip = null;
            return true;
        }

        key = string.Empty;
        displayName = string.Empty;
        tooltip = null;
        return false;
    }

    private static ShaderAuthoringNode CreateProperty(ShaderUiProperty property, int order)
        => new()
        {
            SemanticId = GetPropertySemanticId(property.Name),
            Kind = EShaderAuthoringNodeKind.Property,
            DisplayName = property.DisplayName,
            SourcePropertyName = property.Name,
            SourceType = property.GlslType,
            DefaultValue = property.DefaultLiteral,
            SourceLine = property.SourceLine,
            DeclarationOrder = order,
            ManifestProperty = property,
            Options = new ShaderAuthoringOptions { Tooltip = property.Tooltip },
        };

    private static ShaderAuthoringNode CreateGroup(
        EShaderAuthoringNodeKind kind,
        string semanticName,
        string displayName,
        string? tooltip,
        int order)
        => new()
        {
            SemanticId = $"{SemanticIdPrefix}{semanticName}",
            Kind = kind,
            DisplayName = displayName,
            DeclarationOrder = order,
            Options = new ShaderAuthoringOptions { Tooltip = tooltip },
        };

    private static void AddChild(ShaderAuthoringNode parent, ShaderAuthoringNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
    }

    private static string ComputeFingerprint(string sourceIdentity, ShaderUiManifest manifest)
    {
        StringBuilder builder = new(sourceIdentity);
        foreach (ShaderUiFeature feature in manifest.Features)
            builder.Append("|f:").Append(feature.Id).Append(':').Append(feature.Category);
        foreach (ShaderUiProperty property in manifest.Properties)
            builder.Append("|p:").Append(property.Name).Append(':').Append(property.GlslType).Append(':').Append(property.FeatureId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}
