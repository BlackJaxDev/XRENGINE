using System.Collections.ObjectModel;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public enum EMaterialAuthoringPresetScope
{
    Material,
    Section,
    Subsection,
    SelectedProperties,
}

/// <summary>
/// Bounded, deterministic file-backed preset index. Preset previews and
/// application are separate operations so browsing never mutates a material.
/// </summary>
public sealed class MaterialAuthoringPresetLibrary
{
    private readonly List<MaterialAuthoringPresetEntry> _entries = [];
    private readonly LinkedList<string> _recent = [];
    private readonly int _maximumEntries;
    private readonly int _maximumRecent;

    public MaterialAuthoringPresetLibrary(int maximumEntries = 2048, int maximumRecent = 20)
    {
        _maximumEntries = Math.Clamp(maximumEntries, 1, 16384);
        _maximumRecent = Math.Clamp(maximumRecent, 1, 100);
    }

    public IReadOnlyList<MaterialAuthoringPresetEntry> Entries => _entries;
    public IReadOnlyCollection<string> Recent => _recent;

    public IReadOnlyList<string> Rebuild(string root)
    {
        _entries.Clear();
        List<string> diagnostics = [];
        if (!Directory.Exists(root))
            return diagnostics;

        foreach (string path in Directory.EnumerateFiles(root, "*.xrematerialpreset.json", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            if (_entries.Count >= _maximumEntries)
            {
                diagnostics.Add($"Preset index limit {_maximumEntries} reached.");
                break;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException exception)
            {
                diagnostics.Add($"{Path.GetFileName(path)}: {exception.Message}");
                continue;
            }

            if (!MaterialAuthoringPreset.TryDeserialize(json, out MaterialAuthoringPreset? preset, out string? diagnostic) ||
                preset is null)
            {
                diagnostics.Add($"{Path.GetFileName(path)}: {diagnostic}");
                continue;
            }

            _entries.Add(new(path, preset));
        }

        _entries.Sort(static (left, right) =>
        {
            int collection = string.Compare(left.Preset.Collection, right.Preset.Collection, StringComparison.Ordinal);
            if (collection != 0)
                return collection;
            int name = string.Compare(left.Preset.Name, right.Preset.Name, StringComparison.Ordinal);
            return name != 0 ? name : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
        });
        return diagnostics;
    }

    public IEnumerable<MaterialAuthoringPresetEntry> Search(string? query, string? collection)
    {
        foreach (MaterialAuthoringPresetEntry entry in _entries)
        {
            if (!string.IsNullOrWhiteSpace(collection) &&
                !string.Equals(entry.Preset.Collection, collection, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query) &&
                !entry.Preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !(entry.Preset.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(entry.Preset.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            yield return entry;
        }
    }

    public void MarkUsed(MaterialAuthoringPresetEntry entry)
    {
        _recent.Remove(entry.Path);
        _recent.AddFirst(entry.Path);
        while (_recent.Count > _maximumRecent)
            _recent.RemoveLast();
    }

    public static void Save(
        string projectAssetRoot,
        string path,
        MaterialAuthoringPreset preset,
        bool overwrite)
    {
        string validated = MaterialTexturePacker.ValidateOutputPath(projectAssetRoot, path);
        if (File.Exists(validated) && !overwrite)
            throw new IOException("The preset exists and overwrite was not confirmed.");
        Directory.CreateDirectory(Path.GetDirectoryName(validated)!);
        File.WriteAllText(validated, preset.Serialize());
    }
}

public sealed record MaterialAuthoringPresetEntry(string Path, MaterialAuthoringPreset Preset);

public sealed record MaterialAuthoringPresetConflict(
    string SemanticId,
    int EarlierPresetIndex,
    int LaterPresetIndex,
    string EarlierValue,
    string LaterValue);

public static class MaterialAuthoringPresetSequencer
{
    public static IReadOnlyList<MaterialAuthoringPresetConflict> FindConflicts(
        IReadOnlyList<MaterialAuthoringPreset> presets)
    {
        Dictionary<string, (int Index, string Value)> previous = new(StringComparer.Ordinal);
        List<MaterialAuthoringPresetConflict> conflicts = [];
        for (int presetIndex = 0; presetIndex < presets.Count; presetIndex++)
        {
            foreach (MaterialAuthoringPresetValue value in presets[presetIndex].Values)
            {
                if (!value.Included)
                    continue;
                if (previous.TryGetValue(value.SemanticId, out (int Index, string Value) earlier) &&
                    !string.Equals(earlier.Value, value.SerializedValue, StringComparison.Ordinal))
                    conflicts.Add(new(
                        value.SemanticId,
                        earlier.Index,
                        presetIndex,
                        earlier.Value,
                        value.SerializedValue));
                previous[value.SemanticId] = (presetIndex, value.SerializedValue);
            }
        }
        return conflicts;
    }
}

public sealed class MaterialAuthoringPreviewSession<TTarget, TSnapshot> : IDisposable
    where TTarget : class
{
    private readonly TTarget _target;
    private readonly TSnapshot _before;
    private readonly Action<TTarget, TSnapshot> _restore;
    private bool _committed;
    private bool _disposed;

    public MaterialAuthoringPreviewSession(
        TTarget target,
        Func<TTarget, TSnapshot> capture,
        Action<TTarget, TSnapshot> restore)
    {
        _target = target;
        _restore = restore;
        _before = capture(target);
    }

    public void Preview(Action<TTarget> mutation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _restore(_target, _before);
        mutation(_target);
    }

    public void Apply()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _committed = true;
    }

    public void Revert()
    {
        if (_disposed || _committed)
            return;
        _restore(_target, _before);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Revert();
        _disposed = true;
    }
}

public sealed class MaterialAuthoringPreferences
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public bool SimpleMode { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;
    public bool ShowHelp { get; set; } = true;
    public bool ShowAnimationIndicators { get; set; } = true;
    public bool LargeTexturePreviews { get; set; }
    public bool ConfirmOptimize { get; set; } = true;
    public string Locale { get; set; } = "en";
    public int PreviewMemoryBudgetMegabytes { get; set; } = 128;
    public int ThumbnailLimit { get; set; } = 256;

    public void Validate()
    {
        if (Version != CurrentVersion)
            throw new InvalidDataException($"Preference version {Version} is unsupported.");
        PreviewMemoryBudgetMegabytes = Math.Clamp(PreviewMemoryBudgetMegabytes, 16, 2048);
        ThumbnailLimit = Math.Clamp(ThumbnailLimit, 16, 4096);
    }
}

public sealed record MaterialAuthoringLocaleIssue(
    string Locale,
    string SemanticId,
    string Message);

public sealed class MaterialAuthoringLocaleService
{
    private readonly Dictionary<string, Dictionary<string, string>> _imported =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _overrides =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maximumLocales;
    private readonly int _maximumValuesPerLocale;

    public MaterialAuthoringLocaleService(int maximumLocales = 32, int maximumValuesPerLocale = 8192)
    {
        _maximumLocales = maximumLocales;
        _maximumValuesPerLocale = maximumValuesPerLocale;
    }

    public string FallbackLocale { get; set; } = "en";

    public void ImportSourceLabels(ShaderAuthoringSchema schema, string locale = "en")
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (ShaderAuthoringNode node in schema.DeclarationOrder)
        {
            if (values.Count >= _maximumValuesPerLocale)
                break;
            values[node.SemanticId] = node.DisplayName;
        }
        _imported[locale] = values;
    }

    public IReadOnlyList<MaterialAuthoringLocaleIssue> ImportJson(
        string json,
        string locale,
        ShaderAuthoringSchema schema,
        bool authoringOverride)
    {
        List<MaterialAuthoringLocaleIssue> issues = [];
        if (!_imported.ContainsKey(locale) && _imported.Count >= _maximumLocales)
        {
            issues.Add(new(locale, schema.Root.SemanticId, "Locale limit reached."));
            return issues;
        }

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException exception)
        {
            issues.Add(new(locale, schema.Root.SemanticId, exception.Message));
            return issues;
        }

        if (parsed is null)
            return issues;
        Dictionary<string, string> destination = new(StringComparer.Ordinal);
        foreach ((string key, string value) in parsed.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (destination.Count >= _maximumValuesPerLocale)
            {
                issues.Add(new(locale, key, "Locale value limit reached."));
                break;
            }
            if (!schema.NodeLookup.ContainsKey(key))
                issues.Add(new(locale, key, "Translation key is not present in this schema."));
            destination[key] = value;
        }
        (authoringOverride ? _overrides : _imported)[locale] = destination;
        return issues;
    }

    public string Resolve(string locale, ShaderAuthoringNode node, params object?[] arguments)
    {
        string? value = ResolveCore(_overrides, locale, node.SemanticId) ??
                        ResolveCore(_imported, locale, node.SemanticId) ??
                        ResolveCore(_overrides, FallbackLocale, node.SemanticId) ??
                        ResolveCore(_imported, FallbackLocale, node.SemanticId) ??
                        node.DisplayName;
        try
        {
            return arguments.Length == 0
                ? value
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, value, arguments);
        }
        catch (FormatException)
        {
            return node.DisplayName;
        }
    }

    public IEnumerable<string> SearchTerms(string locale, ShaderAuthoringNode node)
    {
        yield return Resolve(locale, node);
        yield return node.DisplayName;
        yield return node.SemanticId;
        if (node.SourcePropertyName is not null)
            yield return node.SourcePropertyName;
        foreach (string alternative in node.Options.AlternativeLabels)
            yield return alternative;
    }

    public string ExportOverrides(string locale)
        => JsonSerializer.Serialize(
            _overrides.TryGetValue(locale, out Dictionary<string, string>? values)
                ? values
                : new Dictionary<string, string>(),
            new JsonSerializerOptions { WriteIndented = true });

    private static string? ResolveCore(
        IReadOnlyDictionary<string, Dictionary<string, string>> source,
        string locale,
        string semanticId)
        => source.TryGetValue(locale, out Dictionary<string, string>? values) &&
           values.TryGetValue(semanticId, out string? result)
            ? result
            : null;
}

public enum ERemoteAuthoringFacility
{
    LocalMessage,
    RemoteMessage,
    RemoteVersionCheck,
    RemoteImage,
}

public sealed record RemoteAuthoringPolicy(
    bool Enabled,
    IReadOnlySet<string> AllowedDomains,
    int MaximumBytes,
    TimeSpan CacheLifetime)
{
    public string? Validate(Uri uri)
    {
        if (!Enabled)
            return "Remote authoring content is disabled.";
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "Remote authoring content requires HTTPS.";
        if (!AllowedDomains.Contains(uri.Host))
            return $"Domain '{uri.Host}' is not allowlisted.";
        if (MaximumBytes is < 1 or > 16 * 1024 * 1024)
            return "Remote content size policy is invalid.";
        return null;
    }
}

public sealed record MaterialAuthoringLinkMember(string AssetIdentity, string SchemaId);

public sealed record MaterialAuthoringPersistentLinkGroup(
    int Version,
    Guid Id,
    string Name,
    string SemanticPropertyId,
    IReadOnlyList<MaterialAuthoringLinkMember> Members)
{
    public const int CurrentVersion = 1;

    public string Serialize()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record MaterialCleanupItem(
    string Kind,
    string Identity,
    string Description,
    bool IsImportedReconversionData,
    bool Selected);

public sealed class MaterialCleanupReport
{
    public required IReadOnlyList<MaterialCleanupItem> Items { get; init; }

    public bool RequiresImportedMetadataConfirmation
        => Items.Any(static item => item.Selected && item.IsImportedReconversionData);
}

public sealed record MaterialTextureUse(
    string MaterialIdentity,
    string SemanticId,
    string PropertyName,
    string TextureIdentity);

public sealed class MaterialTextureUseIndex
{
    private readonly Dictionary<string, List<MaterialTextureUse>> _uses =
        new(StringComparer.OrdinalIgnoreCase);

    public void Rebuild(IEnumerable<MaterialTextureUse> uses)
    {
        _uses.Clear();
        foreach (MaterialTextureUse use in uses)
        {
            if (!_uses.TryGetValue(use.TextureIdentity, out List<MaterialTextureUse>? entries))
                _uses[use.TextureIdentity] = entries = [];
            entries.Add(use);
        }
        foreach (List<MaterialTextureUse> entries in _uses.Values)
            entries.Sort(static (left, right) =>
            {
                int material = string.Compare(left.MaterialIdentity, right.MaterialIdentity, StringComparison.Ordinal);
                return material != 0
                    ? material
                    : string.Compare(left.SemanticId, right.SemanticId, StringComparison.Ordinal);
            });
    }

    public IReadOnlyList<MaterialTextureUse> Find(string textureIdentity)
        => _uses.TryGetValue(textureIdentity, out List<MaterialTextureUse>? uses)
            ? new ReadOnlyCollection<MaterialTextureUse>(uses)
            : [];
}

public sealed record MaterialVariantPreparationResult(
    XRMaterial Material,
    bool Succeeded,
    EUberMaterialVariantStage Stage,
    ulong VariantHash,
    string? Diagnostic);

public static class MaterialVariantPreparationManager
{
    public static async Task<IReadOnlyList<MaterialVariantPreparationResult>> PrepareAsync(
        IReadOnlyList<XRMaterial> materials,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        List<MaterialVariantPreparationResult> results = new(materials.Count);
        for (int index = 0; index < materials.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XRMaterial material = materials[index];
            bool succeeded = await Task.Run(material.PrepareUberVariantImmediately, cancellationToken)
                .ConfigureAwait(false);
            UberMaterialVariantStatus status = material.UberVariantStatus;
            results.Add(new(
                material,
                succeeded,
                status.Stage,
                status.RequestedVariantHash,
                status.FailureReason));
            progress?.Report((index + 1, materials.Count));
        }
        return results;
    }
}

public readonly record struct MaterialDecalRaycastHit(
    object Renderer,
    int MaterialSlot,
    System.Numerics.Vector3 Position,
    System.Numerics.Vector3 Normal,
    System.Numerics.Vector2 Uv,
    bool MirroredTransform,
    bool IsSkinned);

public interface IMaterialDecalViewportBridge
{
    bool TryRaycast(out MaterialDecalRaycastHit hit);
    void DrawGizmo(DecalTransform transform);
    bool IsMaterialAlive(XRMaterial material);
    bool IsSelectionCompatible(XRMaterial material, int materialSlot);
}

public sealed class MaterialDecalToolController : IDisposable
{
    private readonly XRMaterial _material;
    private readonly IMaterialDecalViewportBridge _viewport;
    private readonly DecalPositioningSession _session;
    private bool _disposed;

    public MaterialDecalToolController(
        XRMaterial material,
        int materialSlot,
        DecalTransform initial,
        Action<XRMaterial, DecalTransform> preview,
        IMaterialDecalViewportBridge viewport)
    {
        _material = material;
        _viewport = viewport;
        _session = new(material, materialSlot, initial, preview);
    }

    public DecalTransform Current => _session.Current;

    public string? UpdateFromRaycast()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? lifetime = ValidateLifetime();
        if (lifetime is not null)
            return lifetime;
        if (!_viewport.TryRaycast(out MaterialDecalRaycastHit hit))
            return "No compatible surface was hit.";
        if (hit.MaterialSlot != _session.MaterialSlot)
            return $"The hit resolved material slot {hit.MaterialSlot}, expected {_session.MaterialSlot}.";

        DecalTransform next = Current with
        {
            Position = hit.Position,
            Mirrored = hit.MirroredTransform,
            UvOffset = hit.Uv,
        };
        _session.Preview(next);
        return hit.IsSkinned
            ? "Placed on a skinned renderer; projection follows material-space UVs."
            : null;
    }

    public string? Preview(DecalTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? lifetime = ValidateLifetime();
        if (lifetime is not null)
            return lifetime;
        if (transform.Scale.X == 0.0f || transform.Scale.Y == 0.0f || transform.Scale.Z == 0.0f)
            return "Decal scale components must be non-zero.";
        _session.Preview(transform);
        _viewport.DrawGizmo(transform);
        return null;
    }

    public bool Commit(out MaterialAuthoringTransactionReport report)
        => _session.Commit(out report);

    public void Cancel() => _session.Cancel();

    private string? ValidateLifetime()
    {
        if (!_viewport.IsMaterialAlive(_material))
            return "The material was disposed or reimported; decal positioning has ended.";
        if (!_viewport.IsSelectionCompatible(_material, _session.MaterialSlot))
            return "The renderer/material selection changed; decal positioning has ended.";
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _session.Dispose();
        _disposed = true;
    }
}

public enum EMaterialWorkflowClassification
{
    Native,
    PreservedInactive,
    DeveloperOnly,
}

public sealed record MaterialWorkflowAuditEntry(
    string Id,
    EMaterialWorkflowClassification Classification,
    string Owner,
    string Validation);

public static class MaterialAuthoringWorkflowAudit
{
    private static readonly Dictionary<string, MaterialWorkflowAuditEntry> Entries =
        new(StringComparer.Ordinal)
        {
            ["workflow:inspectorHierarchy"] = Native("workflow:inspectorHierarchy", "schema-tree"),
            ["workflow:crossMaterialEditor"] = Native("workflow:crossMaterialEditor", "multi-material"),
            ["workflow:decalSceneTool"] = Native("workflow:decalSceneTool", "decal-controller"),
            ["workflow:gradientEditor"] = Native("workflow:gradientEditor", "gradient-curve-bake"),
            ["workflow:localization"] = Native("workflow:localization", "locale-fallback"),
            ["workflow:materialCleanup"] = Native("workflow:materialCleanup", "cleanup-report"),
            ["workflow:materialLinking"] = Native("workflow:materialLinking", "link-cycle"),
            ["workflow:materialNotes"] = Native("workflow:materialNotes", "metadata-notes"),
            ["workflow:materialPresets"] = Native("workflow:materialPresets", "preset-preview"),
            ["workflow:pasteSpecial"] = Native("workflow:pasteSpecial", "clipboard-selection"),
            ["workflow:propertyContextMenu"] = Native("workflow:propertyContextMenu", "property-context"),
            ["workflow:shaderLocking"] = Native("workflow:shaderLocking", "variant-manager"),
            ["workflow:shaderTranslator"] = Native("workflow:shaderTranslator", "semantic-preview"),
            ["workflow:texturePacker"] = Native("workflow:texturePacker", "texture-determinism"),
            ["workflow:textureUseLookup"] = Native("workflow:textureUseLookup", "texture-index"),
            ["workflow:unpreparedMaterialManager"] = Native("workflow:unpreparedMaterialManager", "variant-batch"),
        };

    public static bool TryGet(string id, out MaterialWorkflowAuditEntry entry)
        => Entries.TryGetValue(id, out entry!);

    public static IReadOnlyCollection<MaterialWorkflowAuditEntry> All => Entries.Values;

    private static MaterialWorkflowAuditEntry Native(string id, string validation)
        => new(id, EMaterialWorkflowClassification.Native, "Editor", validation);
}
