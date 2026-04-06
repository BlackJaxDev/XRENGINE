using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Fbx;

public sealed record FbxCorpusEntry(
    string Id,
    string Description,
    FbxCorpusAvailability Availability,
    string? RelativePath,
    bool ExpectedImportSuccess,
    FbxTransportEncoding ExpectedEncoding,
    string? ExpectedVersionText,
    bool IncludeInPerformanceBaseline,
    string? ExpectedSummaryPath,
    IReadOnlyList<FbxCorpusScenario> Scenarios,
    string Notes);

public sealed record FbxCorpusManifest(
    int SchemaVersion,
    string Description,
    IReadOnlyList<FbxCorpusEntry> Entries)
{
    public static FbxCorpusManifest Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        FbxCorpusManifest? manifest = JsonSerializer.Deserialize<FbxCorpusManifest>(stream, FbxCorpusJson.SerializerOptions);
        return manifest ?? throw new InvalidDataException($"Failed to deserialize FBX corpus manifest '{path}'.");
    }
}

public sealed record FbxGoldenSummary(
    string AssetId,
    bool ImportSucceeded,
    FbxTransportEncoding DetectedEncoding,
    string? DetectedVersionText,
    long FileSizeBytes,
    int NodeCount,
    int MeshCount,
    int MaterialCount,
    int AnimationCount,
    int BoneCount,
    long TotalVertices,
    long TotalFaces,
    int MaxHierarchyDepth,
    string Notes);

public static class FbxCorpusJson
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