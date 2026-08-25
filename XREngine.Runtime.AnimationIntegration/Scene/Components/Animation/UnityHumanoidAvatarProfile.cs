using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Avatar-specific Unity humanoid calibration. The profile separates measured
/// neutral-pose and muscle-response data from the geometry-only fallback solver.
/// </summary>
public sealed class UnityHumanoidAvatarProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Source { get; set; } = "UnityMecanim";
    public string AvatarName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public float HumanScale { get; set; }
    public UnityHumanoidAvatarDescription AvatarSettings { get; set; } = new();
    public Dictionary<string, Quaternion> NeutralPoseBoneRotations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Vector3> UnityNeutralBoneLocalPositions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, UnityHumanoidBoneResponseProfile> BoneResponses { get; set; } = new(StringComparer.Ordinal);

    public bool TryGetBoneResponse(string boneName, out UnityHumanoidBoneResponseProfile response)
        => BoneResponses.TryGetValue(boneName, out response!);
}
