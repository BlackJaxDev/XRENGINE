using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XREngine.Rendering;
using XREngine.Scene.Importers.Poiyomi;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Compiles the embedded, pinned Poiyomi 9.3.64 inventory into a safe native
/// authoring tree. Unity editor types are data only; no drawer or action type
/// name from the catalog is reflected or instantiated.
/// </summary>
public static partial class PoiyomiAuthoringSchemaCatalog
{
    private static readonly ConditionalWeakTable<ShaderUiManifest, ShaderAuthoringSchema> Cache = new();
    private static readonly HashSet<string> KnownOptionKeys = new(StringComparer.Ordinal)
    {
        "offset", "tooltip", "altClick", "onClick", "condition_showS", "condition_show",
        "condition_enable", "condition_enable_children", "on_value", "actions",
        "on_value_actions", "button_help", "button_author", "texture", "reference_property",
        "reference_properties", "fps_property", "force_texture_options", "is_visible_simple",
        "file_name", "remote_version_url", "generic_string", "never_lock", "margin_top",
        "alts", "persistent_expand", "default_expand", "ref_float_toggles_expand", "draw_border",
        "action", "data", "hover", "text", "type", "value", "filterMode", "wrapMode", "width", "height",
    };

    private static readonly Dictionary<string, string> SemanticAliases = new(StringComparer.Ordinal)
    {
        ["_Color"] = "_MainColor",
        ["_BaseColor"] = "_MainColor",
        ["_BaseMap"] = "_MainTex",
        ["_BumpMap"] = "_NormalMap",
        ["_BumpScale"] = "_NormalStrength",
        ["_EmissionMap"] = "_EmissionMap",
        ["_EmissionColor"] = "_EmissionColor",
    };

    public static ShaderAuthoringSchema GetOrCreate(ShaderUiManifest manifest)
        => Cache.GetValue(manifest, Build);

    private static ShaderAuthoringSchema Build(ShaderUiManifest manifest)
    {
        using Stream stream = PoiyomiToon93Catalog.OpenCatalog();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement rootElement = document.RootElement;
        int version = rootElement.GetProperty("schemaVersion").GetInt32();
        JsonElement source = rootElement.GetProperty("source");
        string commit = source.GetProperty("commit").GetString() ?? PoiyomiToon93Catalog.RepositoryCommit;
        string shaderVersion = source.GetProperty("shaderVersion").GetString() ?? PoiyomiToon93Catalog.VersionText;
        string sourceIdentity = $"Poiyomi Toon {shaderVersion}@{commit}";

        List<ShaderAuthoringIssue> issues = [];
        ShaderAuthoringNode root = new()
        {
            SemanticId = $"poiyomi/{shaderVersion}/root",
            Kind = EShaderAuthoringNodeKind.Root,
            DisplayName = $"Poiyomi Toon {shaderVersion}",
            DeclarationOrder = -1,
        };

        Stack<(ShaderAuthoringNode Node, string Marker)> hierarchy = new();
        hierarchy.Push((root, string.Empty));
        Dictionary<string, CatalogProperty> sourceProperties = new(StringComparer.Ordinal);
        int order = 0;
        foreach (JsonElement item in rootElement.GetProperty("properties").EnumerateArray())
        {
            CatalogProperty property = ReadProperty(item);
            sourceProperties[property.Name] = property;
            if (TryReadMarker(property.Name, out bool starts, out EShaderAuthoringNodeKind groupKind, out string marker))
            {
                if (starts)
                {
                    ShaderAuthoringNode group = CreateNode(property, groupKind, order++, manifest, issues);
                    AddChild(hierarchy.Peek().Node, group);
                    hierarchy.Push((group, marker));
                }
                else
                {
                    if (hierarchy.Count == 1)
                    {
                        issues.Add(new(
                            EShaderAuthoringIssueSeverity.Error,
                            $"Section end marker '{property.Name}' has no open section.",
                            $"poiyomi/{shaderVersion}/marker/{property.Name}",
                            property.SourceLine));
                        continue;
                    }

                    if (!string.Equals(hierarchy.Peek().Marker, marker, StringComparison.Ordinal))
                    {
                        issues.Add(new(
                            EShaderAuthoringIssueSeverity.Error,
                            $"Section end marker '{property.Name}' overlaps open marker '{hierarchy.Peek().Marker}'.",
                            hierarchy.Peek().Node.SemanticId,
                            property.SourceLine));
                        while (hierarchy.Count > 1 &&
                               !string.Equals(hierarchy.Peek().Marker, marker, StringComparison.Ordinal))
                            hierarchy.Pop();
                    }

                    if (hierarchy.Count > 1)
                        hierarchy.Pop();
                }
                continue;
            }

            ShaderAuthoringNode node = CreateNode(property, ResolveNodeKind(property), order++, manifest, issues);
            AddChild(hierarchy.Peek().Node, node);
        }

        while (hierarchy.Count > 1)
        {
            (ShaderAuthoringNode openNode, string marker) = hierarchy.Pop();
            issues.Add(new(
                EShaderAuthoringIssueSeverity.Error,
                $"Section marker '{marker}' was not closed.",
                openNode.SemanticId,
                openNode.SourceLine));
        }

        ValidateReferences(root, sourceProperties, issues);
        string fingerprint = ComputeFingerprint(sourceIdentity, manifest, root);
        return new ShaderAuthoringSchema(
            $"poiyomi-toon-{shaderVersion}",
            version,
            sourceIdentity,
            fingerprint,
            root,
            issues);
    }

    private static ShaderAuthoringNode CreateNode(
        CatalogProperty property,
        EShaderAuthoringNodeKind kind,
        int order,
        ShaderUiManifest manifest,
        List<ShaderAuthoringIssue> issues)
    {
        ShaderAuthoringOptions options = ParseOptions(property);
        ShaderUiProperty? manifestProperty = ResolveManifestProperty(manifest, property.Name);
        ShaderAuthoringExpression? visibility = CompileExpression(
            options.ConditionShow,
            property,
            "visibility",
            issues);
        ShaderAuthoringExpression? enabled = CompileExpression(
            options.ConditionEnable,
            property,
            "enable",
            issues);
        ShaderAuthoringExpression? enabledChildren = CompileExpression(
            options.ConditionEnableChildren,
            property,
            "child-enable",
            issues);

        string semanticName = manifestProperty?.Name ?? property.Name;
        string displayName = SanitizeRichLabel(
            property.DisplayName.Length > 0 ? property.DisplayName : FormatMarkerName(property.Name));
        string? widget = ResolveWidget(property.Attributes);
        foreach (string optionKey in property.OptionKeys)
        {
            if (!KnownOptionKeys.Contains(optionKey))
            {
                issues.Add(new(
                    EShaderAuthoringIssueSeverity.Info,
                    $"PropertyOptions field '{optionKey}' is preserved but inactive.",
                    $"poiyomi/{PoiyomiToon93Catalog.VersionText}/property/{semanticName}",
                    property.SourceLine));
            }
        }

        if (kind == EShaderAuthoringNodeKind.ToolLauncher &&
            !ShaderAuthoringWidgetRegistry.IsAllowlistedTool(widget))
        {
            issues.Add(new(
                EShaderAuthoringIssueSeverity.Warning,
                $"Tool drawer '{widget}' is active but has no allowlisted engine tool.",
                $"poiyomi/{PoiyomiToon93Catalog.VersionText}/property/{semanticName}",
                property.SourceLine));
        }

        EShaderAuthoringNodeKind resolvedKind =
            kind == EShaderAuthoringNodeKind.Decorator && manifestProperty is not null
                ? EShaderAuthoringNodeKind.Property
                : kind;

        return new ShaderAuthoringNode
        {
            SemanticId = $"poiyomi/{PoiyomiToon93Catalog.VersionText}/{resolvedKind.ToString().ToLowerInvariant()}/{semanticName}",
            Kind = resolvedKind,
            DisplayName = displayName,
            SourcePropertyName = property.Name,
            LocalizationKey = property.LocalizationKey,
            SourceType = property.Type,
            DefaultValue = property.DefaultValue,
            WidgetId = widget,
            Classification = property.Classification,
            SourceLine = property.SourceLine,
            DeclarationOrder = order,
            ManifestProperty = manifestProperty,
            Options = options,
            Attributes = property.Attributes,
            VisibilityExpression = visibility,
            EnableExpression = enabled,
            EnableChildrenExpression = enabledChildren,
        };
    }

    private static ShaderAuthoringExpression? CompileExpression(
        string? source,
        CatalogProperty property,
        string purpose,
        List<ShaderAuthoringIssue> issues)
    {
        if (ShaderAuthoringExpression.TryCompile(source, out ShaderAuthoringExpression? expression, out string? diagnostic))
            return expression;

        issues.Add(new(
            EShaderAuthoringIssueSeverity.Error,
            $"Invalid {purpose} expression: {diagnostic}",
            $"poiyomi/{PoiyomiToon93Catalog.VersionText}/property/{property.Name}",
            property.SourceLine));
        return null;
    }

    private static ShaderUiProperty? ResolveManifestProperty(ShaderUiManifest manifest, string sourceName)
    {
        if (manifest.PropertyLookup.TryGetValue(sourceName, out ShaderUiProperty? exact))
            return exact;
        if (SemanticAliases.TryGetValue(sourceName, out string? alias) &&
            manifest.PropertyLookup.TryGetValue(alias, out ShaderUiProperty? aliased))
            return aliased;
        return null;
    }

    private static EShaderAuthoringNodeKind ResolveNodeKind(CatalogProperty property)
    {
        string? widget = ResolveWidget(property.Attributes);
        if (widget is "Header" or "ThryHeaderLabel" or "ThryRichLabel" or "Helpbox" or "IMPORTANT" or "sRGBWarning" or "Space" or "ThrySpace")
            return EShaderAuthoringNodeKind.Decorator;
        if (widget is "ThryRGBAPacker" or "ThryDecalPositioning" or "ThryCustomGUI" or "ThryExternalTextureTool" or "ThryShaderOptimizerLockButton")
            return EShaderAuthoringNodeKind.ToolLauncher;
        if (property.ActionTypes.Count > 0 && property.Classification == "inspectorOnly")
            return EShaderAuthoringNodeKind.Action;
        return EShaderAuthoringNodeKind.Property;
    }

    private static string? ResolveWidget(IReadOnlyList<ShaderAuthoringAttribute> attributes)
    {
        string? decorator = null;
        foreach (ShaderAuthoringAttribute attribute in attributes)
        {
            if (attribute.Name is "HideInInspector" or "DoNotAnimate" or "DoNotLock" or "DoNotRename" or
                "NoScaleOffset" or "NonModifiableTextureData")
                continue;

            if (attribute.Name is "Helpbox" or "IMPORTANT" or "sRGBWarning" or "Space" or "ThrySpace")
            {
                decorator ??= attribute.Name;
                continue;
            }

            return attribute.Name;
        }
        return decorator;
    }

    private static void AddChild(ShaderAuthoringNode parent, ShaderAuthoringNode child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
    }

    private static bool TryReadMarker(
        string propertyName,
        out bool starts,
        out EShaderAuthoringNodeKind kind,
        out string marker)
    {
        starts = false;
        kind = EShaderAuthoringNodeKind.Section;
        marker = string.Empty;
        string prefix;
        if (propertyName.StartsWith("m_start_", StringComparison.Ordinal))
        {
            starts = true;
            kind = EShaderAuthoringNodeKind.Section;
            prefix = "m_start_";
        }
        else if (propertyName.StartsWith("m_end_", StringComparison.Ordinal))
        {
            prefix = "m_end_";
        }
        else if (propertyName.StartsWith("s_start_", StringComparison.Ordinal))
        {
            starts = true;
            kind = EShaderAuthoringNodeKind.Subsection;
            prefix = "s_start_";
        }
        else if (propertyName.StartsWith("s_end_", StringComparison.Ordinal))
        {
            kind = EShaderAuthoringNodeKind.Subsection;
            prefix = "s_end_";
        }
        else
            return false;

        marker = propertyName[prefix.Length..];
        return true;
    }

    private static ShaderAuthoringOptions ParseOptions(CatalogProperty property)
    {
        string raw = property.DisplayOptions;
        Dictionary<string, string> unknown = new(StringComparer.Ordinal);
        foreach (string key in property.OptionKeys)
        {
            if (!KnownOptionKeys.Contains(key))
                unknown[key] = ExtractOption(raw, key) ?? string.Empty;
        }

        return new ShaderAuthoringOptions
        {
            Offset = ParseFloat(ExtractOption(raw, "offset")),
            Tooltip = Unwrap(ExtractOption(raw, "tooltip")),
            AltClick = ExtractOption(raw, "altClick"),
            OnClick = ExtractOption(raw, "onClick"),
            ConditionShow = Unwrap(ExtractOption(raw, "condition_showS") ?? ExtractOption(raw, "condition_show")),
            ConditionEnable = Unwrap(ExtractOption(raw, "condition_enable")),
            ConditionEnableChildren = Unwrap(ExtractOption(raw, "condition_enable_children")),
            OnValue = ExtractOption(raw, "on_value"),
            Actions = ExtractOption(raw, "actions"),
            OnValueActions = ExtractOption(raw, "on_value_actions"),
            ButtonHelp = ExtractOption(raw, "button_help"),
            ButtonAuthor = ExtractOption(raw, "button_author"),
            Texture = Unwrap(ExtractOption(raw, "texture")),
            ReferenceProperty = Unwrap(ExtractOption(raw, "reference_property")),
            ReferenceProperties = property.PropertyReferences,
            FpsProperty = Unwrap(ExtractOption(raw, "fps_property")),
            ForceTextureOptions = ParseBool(ExtractOption(raw, "force_texture_options")),
            IsVisibleSimple = !property.OptionKeys.Contains("is_visible_simple") ||
                ParseBool(ExtractOption(raw, "is_visible_simple")),
            FileName = Unwrap(ExtractOption(raw, "file_name")),
            RemoteVersionUrl = Unwrap(ExtractOption(raw, "remote_version_url")),
            GenericString = Unwrap(ExtractOption(raw, "generic_string")),
            NeverLock = ParseBool(ExtractOption(raw, "never_lock")),
            MarginTop = ParseFloat(ExtractOption(raw, "margin_top")) ?? 0.0f,
            AlternativeLabels = SplitList(ExtractOption(raw, "alts")),
            PersistentExpand = ParseBool(ExtractOption(raw, "persistent_expand")),
            DefaultExpand = ParseBool(ExtractOption(raw, "default_expand")),
            ReferenceFloatTogglesExpand = ParseBool(ExtractOption(raw, "ref_float_toggles_expand")),
            DrawBorder = ParseBool(ExtractOption(raw, "draw_border")),
            Unclassified = unknown,
        };
    }

    internal static string? ExtractOption(string source, string key)
    {
        if (source.Length == 0)
            return null;

        int keyIndex = source.IndexOf(key, StringComparison.Ordinal);
        while (keyIndex >= 0)
        {
            int cursor = keyIndex + key.Length;
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
                cursor++;
            if (cursor < source.Length && source[cursor] == ':')
            {
                cursor++;
                while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
                    cursor++;
                int start = cursor;
                int round = 0;
                int square = 0;
                int curly = 0;
                char quote = '\0';
                while (cursor < source.Length)
                {
                    char value = source[cursor];
                    if (quote != '\0')
                    {
                        if (value == quote && (cursor == start || source[cursor - 1] != '\\'))
                            quote = '\0';
                    }
                    else
                    {
                        switch (value)
                        {
                            case '"':
                            case '\'':
                                quote = value;
                                break;
                            case '(': round++; break;
                            case ')': round--; break;
                            case '[': square++; break;
                            case ']': square--; break;
                            case '{': curly++; break;
                            case '}':
                                if (round == 0 && square == 0 && curly == 0)
                                    return source[start..cursor].Trim();
                                curly--;
                                break;
                            case ',' when round == 0 && square == 0 && curly == 0:
                                return source[start..cursor].Trim();
                        }
                    }
                    cursor++;
                }
                return source[start..cursor].Trim();
            }
            keyIndex = source.IndexOf(key, keyIndex + key.Length, StringComparison.Ordinal);
        }
        return null;
    }

    private static IReadOnlyList<string> SplitList(string? value)
    {
        string text = Unwrap(value) ?? string.Empty;
        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text[1..^1];
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unwrap)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
    }

    private static string? Unwrap(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string result = value.Trim();
        while (result.Length >= 2 &&
               ((result[0] == '(' && result[^1] == ')') ||
                (result[0] == '"' && result[^1] == '"') ||
                (result[0] == '\'' && result[^1] == '\'')))
            result = result[1..^1].Trim();
        return result;
    }

    private static int? ParseNullableInt(string? value)
        => int.TryParse(
            Unwrap(value),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : null;
    private static bool ParseBool(string? value)
        => bool.TryParse(Unwrap(value), out bool parsed) && parsed;

    private static float? ParseFloat(string? value)
        => float.TryParse(Unwrap(value), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : null;

    private static string SanitizeRichLabel(string value)
    {
        string withoutTags = RichLabelTagRegex().Replace(value, string.Empty);
        return System.Net.WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string FormatMarkerName(string value)
    {
        int index = value.IndexOf('_', value.IndexOf('_') + 1);
        string name = index >= 0 ? value[(index + 1)..] : value;
        return Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
    }

    private static CatalogProperty ReadProperty(JsonElement item)
    {
        List<ShaderAuthoringAttribute> attributes = [];
        foreach (JsonElement attribute in item.GetProperty("attributes").EnumerateArray())
        {
            attributes.Add(new(
                attribute.GetProperty("name").GetString() ?? string.Empty,
                attribute.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind != JsonValueKind.Null
                    ? arguments.GetString()
                    : null));
        }

        return new CatalogProperty(
            item.GetProperty("name").GetString() ?? string.Empty,
            item.GetProperty("sourceLine").GetInt32(),
            item.GetProperty("displayName").GetString() ?? string.Empty,
            item.TryGetProperty("localizationKey", out JsonElement localization) && localization.ValueKind != JsonValueKind.Null
                ? localization.GetString()
                : null,
            item.GetProperty("type").GetString() ?? string.Empty,
            item.GetProperty("defaultValue").GetString() ?? string.Empty,
            attributes,
            item.GetProperty("displayOptions").GetString() ?? string.Empty,
            ReadStrings(item, "optionKeys"),
            ReadStrings(item, "actionTypes"),
            ReadStrings(item, "propertyReferences"),
            item.GetProperty("classification").GetString() ?? string.Empty);
    }

    private static string[] ReadStrings(JsonElement item, string name)
        => item.GetProperty(name).EnumerateArray()
            .Select(static element => element.GetString() ?? string.Empty)
            .ToArray();

    private static void ValidateReferences(
        ShaderAuthoringNode root,
        IReadOnlyDictionary<string, CatalogProperty> sourceProperties,
        List<ShaderAuthoringIssue> issues)
    {
        Dictionary<string, ShaderAuthoringNode> nodes = new(StringComparer.Ordinal);
        Collect(root, nodes);
        foreach (ShaderAuthoringNode node in nodes.Values)
        {
            foreach (string reference in node.Options.ReferenceProperties)
            {
                if (!sourceProperties.ContainsKey(reference))
                {
                    issues.Add(new(
                        EShaderAuthoringIssueSeverity.Warning,
                        $"Referenced property '{reference}' does not exist in the pinned schema.",
                        node.SemanticId,
                        node.SourceLine));
                }
            }
        }

        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (ShaderAuthoringNode node in nodes.Values)
            DetectReferenceCycles(node, nodes, visiting, visited, issues);
    }

    private static void Collect(ShaderAuthoringNode node, IDictionary<string, ShaderAuthoringNode> properties)
    {
        if (node.SourcePropertyName is { Length: > 0 } sourceName)
            properties[sourceName] = node;
        foreach (ShaderAuthoringNode child in node.Children)
            Collect(child, properties);
    }

    private static void DetectReferenceCycles(
        ShaderAuthoringNode node,
        IReadOnlyDictionary<string, ShaderAuthoringNode> nodes,
        ISet<string> visiting,
        ISet<string> visited,
        ICollection<ShaderAuthoringIssue> issues)
    {
        if (node.SourcePropertyName is not { Length: > 0 } sourceName || visited.Contains(sourceName))
            return;
        if (!visiting.Add(sourceName))
        {
            issues.Add(new(
                EShaderAuthoringIssueSeverity.Error,
                $"Reference cycle detected at '{sourceName}'.",
                node.SemanticId,
                node.SourceLine));
            return;
        }
        foreach (string reference in node.Options.ReferenceProperties)
            if (nodes.TryGetValue(reference, out ShaderAuthoringNode? target))
                DetectReferenceCycles(target, nodes, visiting, visited, issues);
        visiting.Remove(sourceName);
        visited.Add(sourceName);
    }

    private static string ComputeFingerprint(
        string sourceIdentity,
        ShaderUiManifest manifest,
        ShaderAuthoringNode root)
    {
        StringBuilder builder = new(sourceIdentity);
        foreach (ShaderUiProperty property in manifest.Properties)
            builder.Append('|').Append(property.Name).Append(':').Append(property.GlslType);
        builder.Append('|').Append(root.Children.Count);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private sealed record CatalogProperty(
        string Name,
        int SourceLine,
        string DisplayName,
        string? LocalizationKey,
        string Type,
        string DefaultValue,
        IReadOnlyList<ShaderAuthoringAttribute> Attributes,
        string DisplayOptions,
        IReadOnlyList<string> OptionKeys,
        IReadOnlyList<string> ActionTypes,
        IReadOnlyList<string> PropertyReferences,
        string Classification);

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex RichLabelTagRegex();
}
