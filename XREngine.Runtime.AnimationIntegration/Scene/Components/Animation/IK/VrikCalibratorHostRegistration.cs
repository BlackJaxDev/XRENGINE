using XREngine.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Registers the direct <see cref="VRIKCalibrator"/> implementation with
/// <see cref="RuntimeVRIKCalibrator"/> before VR IK solver calibration executes.
/// </summary>
internal sealed class VrikCalibratorHostRegistration : IRuntimeVrikCalibrator
{
    private static readonly VrikCalibratorHostRegistration Instance = new();
    private static int _registered;

    internal static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            RuntimeVRIKCalibrator.Register(Instance);
    }

    public VrCalibrationResult Calibrate(
        object solver,
        object? settings,
        TransformBase? headTracker,
        TransformBase? bodyTracker = null,
        TransformBase? leftHandTracker = null,
        TransformBase? rightHandTracker = null,
        TransformBase? leftFootTracker = null,
        TransformBase? rightFootTracker = null)
    {
        if (solver is not VRIKSolverComponent vrikSolver)
            return VrCalibrationResult.Failure("Calibration requires a VRIKSolverComponent instance.");
        if (settings is not VRIKCalibrationSettings calibrationSettings)
            return VrCalibrationResult.Failure("Calibration settings have an unexpected type.");

        return VRIKCalibrator.TryCalibrate(
            vrikSolver,
            calibrationSettings,
            headTracker,
            bodyTracker,
            leftHandTracker,
            rightHandTracker,
            leftFootTracker,
            rightFootTracker);
    }

    public VrCalibrationResult CalibrateSnapshot(
        object solver,
        object? settings,
        VrCalibrationPose[] poses,
        float headTiltToleranceDegrees)
    {
        if (solver is not VRIKSolverComponent vrikSolver)
            return VrCalibrationResult.Failure("Calibration requires a VRIKSolverComponent instance.");
        if (settings is not VRIKCalibrationSettings calibrationSettings)
            return VrCalibrationResult.Failure("Calibration settings have an unexpected type.");

        return VRIKCalibrator.TryCalibrateSnapshot(
            vrikSolver,
            calibrationSettings,
            poses,
            headTiltToleranceDegrees);
    }
}
