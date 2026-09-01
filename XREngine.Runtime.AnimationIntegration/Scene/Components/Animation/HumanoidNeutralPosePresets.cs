using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Describes the embedded part of a neutral-pose preset. Retargeting frames are
/// derived from the mapped avatar at authoring time; no sampled avatar rotations
/// are embedded in the engine.
/// </summary>
public static class HumanoidNeutralPosePresets
{
    private static readonly IReadOnlyDictionary<string, Quaternion> Empty =
        new Dictionary<string, Quaternion>(0, StringComparer.Ordinal);

    /// <summary>
    /// Returns embedded overrides, which are empty for native presets. A native
    /// retargeting preset is avatar-dependent and cannot be a global bone table.
    /// </summary>
    public static IReadOnlyDictionary<string, Quaternion> GetRotations(EHumanoidNeutralPosePreset preset)
        => Empty;

    /// <summary>
    /// Gets the number of embedded overrides, excluding geometry-derived frames.
    /// </summary>
    public static int GetRotationCount(EHumanoidNeutralPosePreset preset) => 0;
}
