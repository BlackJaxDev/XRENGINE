using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Canonical one-time migration result for the former v3 Unity profile.
/// It is definition-owned and never acts as a second mapping authority.
/// </summary>
public sealed class HumanoidAvatarLegacyCalibration
{
    public int SourceSchemaVersion { get; set; }
    public string Source { get; set; } = string.Empty;
    public string AvatarName { get; set; } = string.Empty;
    public string CalibrationClipName { get; set; } = string.Empty;
    public UnityHumanoidClipRootMotionSettings? CalibrationRootMotionSettings { get; set; }
    public UnityHumanoidRootAllocationFrame? RootAllocationFrame { get; set; }
    public HumanoidAvatarLegacyBoneCalibration[] Bones { get; set; } = [];
}
