using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Role-indexed migration payload for a legacy measured avatar profile.
/// Phase 9 removes this fitted response data from production evaluation.
/// </summary>
public sealed class HumanoidAvatarLegacyBoneCalibration
{
    public EHumanoidAvatarBoneRole Role { get; set; }
    public bool HasNeutralRotation { get; set; }
    public Quaternion NeutralRotation { get; set; } = Quaternion.Identity;
    public bool HasNeutralPosition { get; set; }
    public Vector3 NeutralPosition { get; set; }
    public ImportedHumanoidBoneResponseProfile? BoneResponse { get; set; }
    public ImportedHumanoidCoupledBoneModel? CoupledBoneModel { get; set; }
}
