using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Rendering;

namespace XREngine.Scene.Importers.SourceToon;

public enum EMaterialConversionOutcome
{
    Converted,
    ConvertedToSourceToon,
    GenericFallback,
    Failed,
}

public enum EMaterialFeatureParity
{
    Exact,
    NativeEquivalent,
    PreservedInactive,
}

public sealed record MaterialFeatureConversionStatus(
    string FeatureId,
    string FeatureFamily,
    string DisplayName,
    bool SourceEnabled,
    bool RuntimeEnabled,
    EMaterialFeatureParity Parity,
    string NativeEquivalent,
    string SemanticDifference,
    IReadOnlyList<string> SourceProperties);

public sealed record MaterialPreservedValue(
    string SourceProperty,
    string SemanticProperty,
    string ValueKind,
    string SerializedValue,
    string Reason);

public sealed record MaterialConversionCounters(
    int EnabledSourceFeatures,
    int GeneratedFeatures,
    int SamplerPressure,
    int GeneratedVariants,
    int GeneratedPasses,
    int UnsupportedIntegrations);

public sealed record MaterialConversionDiagnosticGroup(
    string MaterialName,
    string FeatureFamily,
    IReadOnlyList<MaterialConversionDiagnostic> Diagnostics);

/// <summary>
/// Deterministic, machine-readable result of one material conversion. The
/// report intentionally excludes timestamps so equal source and converter
/// inputs produce byte-stable JSON.
/// </summary>
public sealed class MaterialConversionReport
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string ConverterId { get; init; } = MaterialConversionReportBuilder.ConverterId;
    public string ConverterVersion { get; init; } = MaterialConversionReportBuilder.ConverterVersion;
    public int SourceDescriptorVersion { get; init; } = MaterialConversionReportBuilder.SourceDescriptorVersion;
    public string MaterialName { get; init; } = string.Empty;
    public string SourceAssetPath { get; init; } = string.Empty;
    public string SourceContentSha256 { get; init; } = string.Empty;
    public string SourceShaderFamily { get; init; } = string.Empty;
    public string SourceShaderVersion { get; init; } = string.Empty;
    public string? SourceShaderPath { get; init; }
    public bool SourceWasLocked { get; init; }
    public EMaterialConversionOutcome Outcome { get; init; }
    public IReadOnlyList<string> GeneratedFeatures { get; init; } = [];
    public IReadOnlyList<string> GeneratedPasses { get; init; } = [];
    public IReadOnlyList<MaterialFeatureConversionStatus> Features { get; init; } = [];
    public IReadOnlyList<MaterialPreservedValue> PreservedInactiveValues { get; init; } = [];
    public IReadOnlyList<MaterialConversionDiagnosticGroup> DiagnosticGroups { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Failures { get; init; } = [];
    public MaterialConversionCounters Counters { get; init; } = new(0, 0, 0, 0, 0, 0);

    [JsonIgnore]
    public bool Succeeded => Outcome is not EMaterialConversionOutcome.Failed;

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = indented,
                Converters = { new JsonStringEnumConverter() },
            });

    public static bool TryParse(string json, out MaterialConversionReport? report)
    {
        report = null;
        try
        {
            report = JsonSerializer.Deserialize<MaterialConversionReport>(
                json,
                new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                });
            return report?.FormatVersion == CurrentFormatVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Associates reports with ordinary converted materials without requiring the
/// rendering runtime to depend on editor/importer types.
/// </summary>
public sealed class MaterialConversionReportRegistry
{
    private readonly ConditionalWeakTable<XRMaterial, MaterialConversionReport> _reports = new();

    public static MaterialConversionReportRegistry Instance { get; } = new();

    public void Set(XRMaterial material, MaterialConversionReport report)
    {
        _reports.Remove(material);
        _reports.Add(material, report);
    }

    public bool TryGet(XRMaterial material, out MaterialConversionReport report)
        => _reports.TryGetValue(material, out report!);
}

public static class MaterialConversionReportBuilder
{
    public const string ConverterId = "xrengine.poiyomi-toon";
    public const string ConverterVersion = "1.0.0";
    public const int SourceDescriptorVersion = 1;

    private static readonly Lazy<IReadOnlySet<string>> PreservedRuntimeProperties =
        new(LoadPreservedRuntimeProperties, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly HashSet<string> ExactFeatures = new(StringComparer.Ordinal)
    {
        "normal-map",
        "alpha-masks",
        "color-adjustments",
        "material-ao",
        "shadow-masks",
        "emission",
        "detail-textures",
        "backface",
        "flipbook",
        "dissolve",
        "parallax",
        "surface-extensions",
        "global-masks-themes",
        "layered-decals",
        "layered-emission",
        "texture-array-flipbook",
        "extended-effects",
        "vertex-effects",
        "view-context",
    };

    private static readonly Dictionary<string, string> SemanticDifferences =
        new(StringComparer.Ordinal)
        {
            ["stylized-shading"] = "Evaluated in XRENGINE's forward-plus light model rather than Unity forward-base/add passes.",
            ["advanced-specular"] = "Uses the engine PBR/light-probe contract rather than Unity reflection-probe macros.",
            ["matcap"] = "Uses engine view-space normals and texture bindings.",
            ["rim-lighting"] = "Uses engine camera/view context and stereo-safe view vectors.",
            ["outline"] = "Rendered as an engine material pass sharing authored state.",
            ["subsurface"] = "Portable screen-independent approximation; Unity light macros are not retained.",
            ["glitter"] = "Portable deterministic noise implementation replaces Unity-specific derivatives.",
            ["advanced-stylized-lighting"] = "Poiyomi lighting semantics are mapped onto XRENGINE forward-plus lights.",
            ["advanced-pbr"] = "Poiyomi/Mochie controls feed XRENGINE's portable BRDF implementation.",
            ["layered-matcap-rim"] = "Repeated slots share engine arrays and semantic bindings.",
            ["audiolink"] = "Requires the registered runtime AudioLink adapter.",
            ["environment-lighting"] = "Requires registered environment/LTCGI/light-volume adapters.",
        };

    public static MaterialConversionReport Create(
        string sourcePath,
        string? shaderPath,
        XRMaterial material,
        SourceToonMaterialDescriptor? descriptor,
        IReadOnlyList<string> warnings,
        IReadOnlyList<MaterialConversionDiagnostic> diagnostics,
        EMaterialConversionOutcome outcome,
        SourceToonShaderMatchResult? sourceMatch = null)
    {
        ShaderUiManifest manifest = material.TryGetUberMaterialState(out _, out ShaderUiManifest resolved)
            ? resolved
            : ShaderUiManifest.Empty;
        MaterialConversionDiagnostic[] effectiveDiagnostics = BuildEffectiveDiagnostics(
            descriptor,
            manifest,
            diagnostics);
        List<MaterialFeatureConversionStatus> features = [];
        foreach (UberMaterialFeatureState feature in material.UberAuthoredState.Features
                     .Where(static feature => feature.Enabled)
                     .OrderBy(static feature => feature.Id, StringComparer.Ordinal))
        {
            ShaderUiFeature? manifestFeature =
                manifest.FeatureLookup.TryGetValue(feature.Id, out ShaderUiFeature? found) ? found : null;
            EMaterialFeatureParity parity = ExactFeatures.Contains(feature.Id)
                ? EMaterialFeatureParity.Exact
                : EMaterialFeatureParity.NativeEquivalent;
            features.Add(new(
                feature.Id,
                ResolveFeatureFamily(feature.Id, manifestFeature?.Category),
                manifestFeature?.DisplayName ?? feature.Id,
                true,
                true,
                parity,
                manifestFeature?.Tooltip ?? $"Native uber feature '{feature.Id}'.",
                SemanticDifferences.GetValueOrDefault(feature.Id) ?? "No material-visible semantic difference is expected.",
                []));
        }

        foreach (MaterialConversionDiagnostic diagnostic in effectiveDiagnostics
                     .Where(static diagnostic => diagnostic.Code == MaterialConversionDiagnosticCodes.IntegrationUnavailable)
                     .OrderBy(static diagnostic => diagnostic.SourceProperty, StringComparer.Ordinal))
        {
            string id = diagnostic.SourceProperty ?? diagnostic.Code;
            if (features.Any(feature => feature.FeatureId == id &&
                                        feature.Parity == EMaterialFeatureParity.PreservedInactive))
                continue;
            features.Add(new(
                id,
                "Runtime Integration",
                id,
                true,
                false,
                EMaterialFeatureParity.PreservedInactive,
                "Source value and identity are retained for a future registered runtime adapter.",
                diagnostic.Message,
                diagnostic.SourceProperty is null ? [] : [diagnostic.SourceProperty]));
        }
        features.Sort(static (left, right) =>
        {
            int family = string.Compare(left.FeatureFamily, right.FeatureFamily, StringComparison.Ordinal);
            return family != 0
                ? family
                : string.Compare(left.FeatureId, right.FeatureId, StringComparison.Ordinal);
        });

        IReadOnlyList<MaterialPreservedValue> preserved =
            descriptor is null ? [] : CollectPreservedValues(descriptor, manifest);
        MaterialConversionDiagnosticGroup[] groups = effectiveDiagnostics
            .GroupBy(diagnostic => ResolveDiagnosticFamily(diagnostic.SourceProperty))
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new MaterialConversionDiagnosticGroup(
                material.Name ?? descriptor?.Name ?? Path.GetFileNameWithoutExtension(sourcePath),
                group.Key,
                group.OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .ThenBy(static diagnostic => diagnostic.SourceProperty, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        string[] generatedFeatures = features
            .Where(static feature => feature.RuntimeEnabled)
            .Select(static feature => feature.FeatureId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] passes = material.PassSet.Passes
            .Where(static pass => pass.Enabled)
            .Select(static pass => pass.Identity.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] failures = effectiveDiagnostics
            .Where(static diagnostic => diagnostic.Severity == MaterialConversionDiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] orderedWarnings = warnings
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        int unsupported = features.Count(static feature =>
            feature.Parity == EMaterialFeatureParity.PreservedInactive);

        return new()
        {
            MaterialName = material.Name ?? descriptor?.Name ?? Path.GetFileNameWithoutExtension(sourcePath),
            SourceAssetPath = Path.GetFullPath(sourcePath),
            SourceContentSha256 = ComputeSha256(sourcePath),
            SourceShaderFamily = sourceMatch?.SourceFamily switch
            {
                SourceToonShaderFamily.Pro => "Poiyomi Pro (lossy downgrade input)",
                SourceToonShaderFamily.Toon => "Poiyomi Toon",
                _ => descriptor is null ? "Unity Shader" : "Poiyomi Toon",
            },
            SourceShaderVersion = sourceMatch?.Version?.ToString() ?? descriptor?.Version.ToString() ?? "unknown",
            SourceShaderPath = shaderPath,
            SourceWasLocked = sourceMatch?.IsLocked ?? descriptor?.IsLocked ?? false,
            Outcome = failures.Length > 0 &&
                      outcome is EMaterialConversionOutcome.Converted or EMaterialConversionOutcome.ConvertedToSourceToon
                ? EMaterialConversionOutcome.Failed
                : outcome,
            GeneratedFeatures = generatedFeatures,
            GeneratedPasses = passes,
            Features = features,
            PreservedInactiveValues = preserved,
            DiagnosticGroups = groups,
            Warnings = orderedWarnings,
            Failures = failures,
            Counters = new(
                features.Count(static feature => feature.SourceEnabled),
                generatedFeatures.Length,
                material.Textures.Count(static texture => texture is not null),
                material.TryGetUberMaterialState(out _, out _) ? 1 : 0,
                passes.Length,
                unsupported),
        };
    }

    public static MaterialConversionReport CreateFailure(
        string sourcePath,
        string message)
        => new()
        {
            MaterialName = Path.GetFileNameWithoutExtension(sourcePath),
            SourceAssetPath = Path.GetFullPath(sourcePath),
            SourceContentSha256 = ComputeSha256(sourcePath),
            SourceShaderFamily = "Unknown",
            SourceShaderVersion = "unknown",
            Outcome = EMaterialConversionOutcome.Failed,
            Failures = [message],
        };

    private static IReadOnlyList<MaterialPreservedValue> CollectPreservedValues(
        SourceToonMaterialDescriptor descriptor,
        ShaderUiManifest manifest)
    {
        List<MaterialPreservedValue> values = [];
        foreach ((string source, SourceToonPropertyBinding binding) in descriptor.PropertyBindings
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (manifest.PropertyLookup.ContainsKey(binding.SemanticName))
                continue;
            if (!TrySerializeDescriptorValue(descriptor, binding.SemanticName, out string kind, out string serialized))
                continue;
            values.Add(new(
                source,
                binding.SemanticName,
                kind,
                serialized,
                "No direct active uber manifest binding; retained in the versioned source descriptor."));
        }
        return values;
    }

    private static MaterialConversionDiagnostic[] BuildEffectiveDiagnostics(
        SourceToonMaterialDescriptor? descriptor,
        ShaderUiManifest manifest,
        IReadOnlyList<MaterialConversionDiagnostic> diagnostics)
    {
        if (descriptor is null)
            return [.. diagnostics];

        string[] preserved = descriptor.PropertyBindings.Values
            .Select(static binding => binding.SemanticName)
            .Where(name => PreservedRuntimeProperties.Value.Contains(name) &&
                           !manifest.PropertyLookup.ContainsKey(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (preserved.Length == 0 || diagnostics.Any(static diagnostic =>
                diagnostic.Code == MaterialConversionDiagnosticCodes.RuntimeMappingMissing))
            return [.. diagnostics];

        const int previewLimit = 8;
        string preview = string.Join(", ", preserved.Take(previewLimit));
        if (preserved.Length > previewLimit)
            preview += $", and {preserved.Length - previewLimit} more";

        return
        [
            .. diagnostics,
            new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.RuntimeMappingMissing,
                MaterialConversionDiagnosticSeverity.Warning,
                $"{preserved.Length} runtime-visible source properties have no active semantic mapping. " +
                $"Their exact values remain in the versioned descriptor: {preview}.",
                preserved[0]),
        ];
    }

    private static IReadOnlySet<string> LoadPreservedRuntimeProperties()
    {
        using Stream stream = SourceToon93Catalog.OpenCatalog();
        using JsonDocument document = JsonDocument.Parse(stream);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement property in document.RootElement.GetProperty("properties").EnumerateArray())
        {
            string classification = property.GetProperty("classification").GetString() ?? string.Empty;
            string parity = property.GetProperty("initialParity").GetString() ?? string.Empty;
            if (parity != "missing" || classification is not ("runtime" or "renderState" or "animationLocking"))
                continue;

            string? name = property.GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    private static bool TrySerializeDescriptorValue(
        SourceToonMaterialDescriptor descriptor,
        string semanticName,
        out string kind,
        out string serialized)
    {
        if (descriptor.Floats.TryGetValue(semanticName, out float number))
        {
            kind = "float";
            serialized = JsonSerializer.Serialize(number);
            return true;
        }
        if (descriptor.Ints.TryGetValue(semanticName, out int integer))
        {
            kind = "int";
            serialized = JsonSerializer.Serialize(integer);
            return true;
        }
        if (descriptor.Vectors.TryGetValue(semanticName, out System.Numerics.Vector4 vector))
        {
            kind = "vector4";
            serialized = JsonSerializer.Serialize(vector);
            return true;
        }
        if (descriptor.Strings.TryGetValue(semanticName, out string? text))
        {
            kind = "string";
            serialized = JsonSerializer.Serialize(text);
            return true;
        }
        if (descriptor.Textures.TryGetValue(semanticName, out SourceToonTextureDescriptor? texture))
        {
            kind = "texture";
            serialized = JsonSerializer.Serialize(new
            {
                texture.Reference.Guid,
                texture.Reference.FileId,
                texture.ResolvedAsset.AssetPath,
                texture.Scale,
                texture.Offset,
            });
            return true;
        }
        kind = string.Empty;
        serialized = string.Empty;
        return false;
    }

    private static string ResolveFeatureFamily(string featureId, string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
            return category;
        if (featureId.Contains("outline", StringComparison.OrdinalIgnoreCase))
            return "Outline";
        if (featureId.Contains("vertex", StringComparison.OrdinalIgnoreCase))
            return "Vertex";
        if (featureId.Contains("pbr", StringComparison.OrdinalIgnoreCase) ||
            featureId.Contains("lighting", StringComparison.OrdinalIgnoreCase))
            return "Lighting";
        if (featureId.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
            featureId.Contains("environment", StringComparison.OrdinalIgnoreCase))
            return "Runtime Integration";
        return "Surface";
    }

    private static string ResolveDiagnosticFamily(string? sourceProperty)
    {
        if (string.IsNullOrWhiteSpace(sourceProperty))
            return "Material";
        if (sourceProperty.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            sourceProperty.Contains("LTCGI", StringComparison.OrdinalIgnoreCase) ||
            sourceProperty.Contains("LightVolume", StringComparison.OrdinalIgnoreCase))
            return "Runtime Integration";
        if (sourceProperty.Contains("Outline", StringComparison.OrdinalIgnoreCase))
            return "Outline";
        if (sourceProperty.Contains("Decal", StringComparison.OrdinalIgnoreCase))
            return "Decals";
        if (sourceProperty.Contains("Emission", StringComparison.OrdinalIgnoreCase))
            return "Emission";
        if (sourceProperty.Contains("Vertex", StringComparison.OrdinalIgnoreCase))
            return "Vertex";
        return "Surface";
    }

    private static string ComputeSha256(string path)
    {
        if (!File.Exists(path))
            return string.Empty;
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
