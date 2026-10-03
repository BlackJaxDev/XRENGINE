using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Typed runtime boundary for capturing and updating the local VR rig.</summary>
public interface IVRIKCalibrationHandle : IVRIKSolverHandle
{
    VrCalibrationResult Calibrate(VrCalibrationRequest request);
    object? CalibrateLegacy(VrCalibrationRequest request);
    VrCalibrationResult ApplyCanonicalCalibrationPose(Matrix4x4 headWorld, Vector3 eyeOffsetFromHead = default);
    void EndCalibrationPose();
    bool TryGetSlot(EHumanoidIKTarget slot, out VrCalibrationSlotState state);
    bool UpdateSlot(EHumanoidIKTarget slot, Matrix4x4 deviceWorld, float weight);
    bool UpdateEstimatedSlot(EHumanoidIKTarget slot, Matrix4x4 targetWorld, float weight);
    bool RebindSlotDevice(EHumanoidIKTarget slot, XREngine.Scene.Transforms.TransformBase device, string identity);
    bool SetSlotWeight(EHumanoidIKTarget slot, float weight);
}
