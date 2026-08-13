using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Deterministic scene and render-feature inputs owned by a profile recipe.</summary>
public sealed record RenderProfileSceneConfiguration
{
    [JsonPropertyName("scene_identity")]
    public string SceneIdentity { get; init; } = "synthetic:no-world";

    [JsonPropertyName("camera_identity")]
    public string CameraIdentity { get; init; } = "synthetic:fixed-camera";

    [JsonPropertyName("light_identities")]
    public string[] LightIdentities { get; init; } = [];

    [JsonPropertyName("animation_identity")]
    public string AnimationIdentity { get; init; } = "frozen";

    [JsonPropertyName("fixed_time_step_seconds")]
    public double FixedTimeStepSeconds { get; init; } = 1.0 / 60.0;

    [JsonPropertyName("random_seed")]
    public int RandomSeed { get; init; } = 0x585245;

    [JsonPropertyName("mesh_strategy")]
    public RenderProfileMeshStrategy MeshStrategy { get; init; } = RenderProfileMeshStrategy.Direct;

    [JsonPropertyName("render_features")]
    public string[] RenderFeatures { get; init; } = [];

    [JsonPropertyName("stereo_mode")]
    public RenderProfileStereoMode StereoMode { get; init; } = RenderProfileStereoMode.Mono;

    [JsonPropertyName("output_identities")]
    public string[] OutputIdentities { get; init; } = ["color0"];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SceneIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(CameraIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(AnimationIdentity);
        if (!double.IsFinite(FixedTimeStepSeconds) || FixedTimeStepSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(FixedTimeStepSeconds));
        if (LightIdentities is null || RenderFeatures is null || OutputIdentities is null || OutputIdentities.Length == 0)
            throw new ArgumentException("Scene lists and at least one output identity are required.");
        if (LightIdentities.Any(string.IsNullOrWhiteSpace) || RenderFeatures.Any(string.IsNullOrWhiteSpace) || OutputIdentities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scene identity lists may not contain empty values.");
    }
}
