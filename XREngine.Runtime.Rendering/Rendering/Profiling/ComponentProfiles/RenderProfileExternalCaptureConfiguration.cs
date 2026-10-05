using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Existing external GPU-capture files to attach after measured work completes.</summary>
public sealed record RenderProfileExternalCaptureConfiguration
{
    [JsonPropertyName("tool_identity")]
    public string? ToolIdentity { get; init; }

    [JsonPropertyName("artifact_paths")]
    public string[] ArtifactPaths { get; init; } = [];

    [JsonPropertyName("require_artifacts")]
    public bool RequireArtifacts { get; init; }

    [JsonIgnore]
    public bool IsRequested => !string.IsNullOrWhiteSpace(ToolIdentity);

    public void Validate()
    {
        if (ArtifactPaths is null || ArtifactPaths.Length > 16 ||
            ArtifactPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("External capture accepts at most 16 non-empty artifact paths.");
        if (ToolIdentity is not null && string.IsNullOrWhiteSpace(ToolIdentity))
            throw new ArgumentException("External capture tool identity must be non-empty.");
        if ((ArtifactPaths.Length != 0 || RequireArtifacts) && !IsRequested)
            throw new ArgumentException("External capture artifact paths require a tool identity.");
        if (IsRequested && ArtifactPaths.Length == 0)
            throw new ArgumentException("External capture tool identity requires at least one artifact path.");
    }
}
