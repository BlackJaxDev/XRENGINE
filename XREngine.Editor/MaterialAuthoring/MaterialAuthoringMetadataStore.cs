using System.Runtime.CompilerServices;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Editor-owned metadata that has no runtime shader representation. Imported
/// Unity identity and local authoring state are retained separately so cleanup
/// and reimport cannot erase them accidentally.
/// </summary>
public sealed class MaterialAuthoringMetadataStore
{
    private readonly ConditionalWeakTable<XRMaterial, MaterialAuthoringMetadata> _metadata = new();

    public static MaterialAuthoringMetadataStore Instance { get; } = new();

    public MaterialAuthoringMetadata Get(XRMaterial material)
        => _metadata.GetValue(material, static _ => new());

    public void SetTag(XRMaterial material, string name, string? value)
    {
        MaterialAuthoringMetadata metadata = Get(material);
        if (string.IsNullOrWhiteSpace(value))
            metadata.Tags.Remove(name);
        else
            metadata.Tags[name] = value;
    }

    public void SetImportedRenderQueue(XRMaterial material, int queue)
        => Get(material).ImportedRenderQueue = queue;
}

public sealed class MaterialAuthoringMetadata
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public int? ImportedRenderQueue { get; set; }
    public string? ImportedShaderIdentity { get; set; }
    public string? ImportedLockIdentity { get; set; }
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ImportedTags { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Notes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LocalOverrides { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> DoNotAnimate { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> DoNotLock { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> DoNotRename { get; init; } = new(StringComparer.Ordinal);

    public string Serialize()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static bool TryDeserialize(string json, out MaterialAuthoringMetadata? metadata)
    {
        metadata = null;
        try
        {
            metadata = JsonSerializer.Deserialize<MaterialAuthoringMetadata>(json);
            return metadata?.Version == CurrentVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
