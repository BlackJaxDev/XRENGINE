namespace XREngine.Components.Animation;

/// <summary>
/// Optional native body-frame trace captured with one audit sample. Every
/// position and rotation is expressed in model-root coordinates except
/// <see cref="FinalHipsLocalPosition"/> and <see cref="FinalHipsLocalRotation"/>,
/// which are Hips-parent local, and the projected-root fields, which use the
/// projected-root coordinate contract.
/// </summary>
public sealed class HumanoidPoseAuditBodyFrame
{
    public string ModelId { get; set; } = string.Empty;
    public int AlgorithmVersion { get; set; }
    public HumanoidPoseAuditVector3 ProvisionalBodyCenter { get; set; } = new();
    public HumanoidPoseAuditQuaternion ProvisionalBodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 RequestedBodyBeforeProjectionCenter { get; set; } = new();
    public HumanoidPoseAuditQuaternion RequestedBodyBeforeProjectionRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 RequestedBodyCenter { get; set; } = new();
    public HumanoidPoseAuditQuaternion RequestedBodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 CompensatedBodyCenter { get; set; } = new();
    public HumanoidPoseAuditQuaternion CompensatedBodyRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 CompensationPosition { get; set; } = new();
    public HumanoidPoseAuditQuaternion CompensationRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 FinalHipsLocalPosition { get; set; } = new();
    public HumanoidPoseAuditQuaternion FinalHipsLocalRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 FinalHipsModelRootPosition { get; set; } = new();
    public HumanoidPoseAuditQuaternion FinalHipsModelRootRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public HumanoidPoseAuditVector3 ProjectedRootPosition { get; set; } = new();
    public HumanoidPoseAuditQuaternion ProjectedRootRotation { get; set; } = HumanoidPoseAuditQuaternion.Identity;
    public int ProjectedRootChannels { get; set; }
}
