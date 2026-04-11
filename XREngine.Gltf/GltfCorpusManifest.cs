using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Gltf;

public sealed record GltfCorpusEntry(
    string Id,
    string Description,
    GltfCorpusAvailability Availability,
    string? RelativePath,
    bool ExpectedImportSuccess,
    GltfAssetContainerKind Container,
    bool IncludeInPerformanceBaseline,
    string? ExpectedSummaryPath,
    IReadOnlyList<GltfCorpusScenario> Scenarios,
    string Notes);

public sealed record GltfCorpusManifest(
    int SchemaVersion,
    string Description,
    IReadOnlyList<GltfCorpusEntry> Entries)
{
    public static GltfCorpusManifest Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        GltfCorpusManifest? manifest = JsonSerializer.Deserialize<GltfCorpusManifest>(stream, GltfCorpusJson.SerializerOptions);
        return manifest ?? throw new InvalidDataException($"Failed to deserialize glTF corpus manifest '{path}'.");
    }
}

public sealed record GltfGoldenSummary(
    string AssetId,
    bool ImportSucceeded,
    GltfAssetContainerKind Container,
    long FileSizeBytes,
    int NodeCount,
    int MeshCount,
    int MaterialCount,
    int AnimationCount,
    int SkinCount,
    int BoneCount,
    int MorphTargetCount,
    long TotalVertices,
    long TotalTriangles,
    int MaxHierarchyDepth,
    int TextureCount,
    IReadOnlyList<string> UsedExtensions,
    string Notes);

public static class GltfCorpusJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}