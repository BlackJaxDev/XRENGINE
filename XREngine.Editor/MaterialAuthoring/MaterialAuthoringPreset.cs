using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

public sealed class MaterialAuthoringPreset
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Name { get; init; } = "Material Preset";
    public string SchemaId { get; init; } = string.Empty;
    public string SchemaFingerprint { get; init; } = string.Empty;
    public string? Collection { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<MaterialAuthoringPresetValue> Values { get; init; } = [];
    public Dictionary<string, bool> Features { get; init; } = new(StringComparer.Ordinal);
    public int? RenderPass { get; init; }

    public string Serialize()
        => JsonSerializer.Serialize(this, SerializerOptions);

    public static bool TryDeserialize(string json, out MaterialAuthoringPreset? preset, out string? diagnostic)
    {
        preset = null;
        diagnostic = null;
        try
        {
            preset = JsonSerializer.Deserialize<MaterialAuthoringPreset>(json, SerializerOptions);
            if (preset is null)
            {
                diagnostic = "Payload did not contain a preset.";
                return false;
            }

            if (preset.Version != CurrentVersion)
            {
                diagnostic = $"Preset version {preset.Version} is not supported; expected {CurrentVersion}.";
                preset = null;
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    public MaterialAuthoringPresetDiff Preview(
        ShaderAuthoringSchema schema,
        IReadOnlyDictionary<string, object?> currentValues)
    {
        List<MaterialAuthoringPresetDiffEntry> entries = [];
        foreach (MaterialAuthoringPresetValue value in Values)
        {
            if (!schema.NodeLookup.TryGetValue(value.SemanticId, out ShaderAuthoringNode? node))
            {
                entries.Add(new(value.SemanticId, null, value.SerializedValue, "Semantic property is unavailable."));
                continue;
            }

            currentValues.TryGetValue(node.SemanticId, out object? current);
            entries.Add(new(
                node.SemanticId,
                Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture),
                value.SerializedValue,
                null));
        }

        return new(entries);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed record MaterialAuthoringPresetValue(
    string SemanticId,
    string ValueType,
    string SerializedValue,
    string? AssetReference,
    EShaderUiPropertyMode? Mode,
    bool Included = true);

public sealed record MaterialAuthoringPresetDiffEntry(
    string SemanticId,
    string? Before,
    string? After,
    string? Diagnostic)
{
    public bool IsCompatible => Diagnostic is null;
    public bool ChangesValue => IsCompatible && !string.Equals(Before, After, StringComparison.Ordinal);
}

public sealed record MaterialAuthoringPresetDiff(
    IReadOnlyList<MaterialAuthoringPresetDiffEntry> Entries);

public sealed class MaterialAuthoringClipboardPayload
{
    public const string Prefix = "XRENGINE_MATERIAL_AUTHORING_V1:";

    public int Version { get; init; } = 1;
    public string SchemaId { get; init; } = string.Empty;
    public string ScopeSemanticId { get; init; } = string.Empty;
    public List<MaterialAuthoringPresetValue> Values { get; init; } = [];

    public string Serialize()
        => Prefix + JsonSerializer.Serialize(this);

    public static bool TryDeserialize(string? text, out MaterialAuthoringClipboardPayload? payload)
    {
        payload = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            payload = JsonSerializer.Deserialize<MaterialAuthoringClipboardPayload>(text[Prefix.Length..]);
            return payload?.Version == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
