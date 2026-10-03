using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Core;

/// <summary>Invokes the animation integration through its typed runtime contract.</summary>
public static class RuntimeVRIKCalibrator
{
    public static VrCalibrationResult Calibrate(object solver, VrCalibrationRequest request)
        => solver is IVRIKCalibrationHandle handle
            ? handle.Calibrate(request)
            : VrCalibrationResult.Failed("The VR solver does not support calibration.");

    /// <summary>Compatibility entry point for callers that supply the original six devices.</summary>
    public static object? Calibrate(
        object solver,
        object? settings,
        TransformBase? headTracker,
        TransformBase? bodyTracker = null,
        TransformBase? leftHandTracker = null,
        TransformBase? rightHandTracker = null,
        TransformBase? leftFootTracker = null,
        TransformBase? rightFootTracker = null)
    {
        VrCalibrationRequest request = new() { Settings = settings };
        Add(EHumanoidIKTarget.Head, headTracker);
        Add(EHumanoidIKTarget.Hips, bodyTracker);
        Add(EHumanoidIKTarget.LeftHand, leftHandTracker);
        Add(EHumanoidIKTarget.RightHand, rightHandTracker);
        Add(EHumanoidIKTarget.LeftFoot, leftFootTracker);
        Add(EHumanoidIKTarget.RightFoot, rightFootTracker);
        return solver is IVRIKCalibrationHandle handle ? handle.CalibrateLegacy(request) : null;

        void Add(EHumanoidIKTarget slot, TransformBase? device)
        {
            if (device is not null)
                request.Slots[(int)slot] = new VrCalibrationCapture(device, device.WorldMatrix);
        }
    }
}
