using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Core;

/// <summary>
/// Provides direct VR IK calibration without reflective type resolution so
/// runtime calibration works under strict NativeAOT parity checks.
/// </summary>
public interface IRuntimeVrikCalibrator
{
    /// <summary>
    /// Calibrates live tracking transforms against the given VR IK solver and settings.
    /// </summary>
    VrCalibrationResult Calibrate(
        object solver,
        object? settings,
        TransformBase? headTracker,
        TransformBase? bodyTracker = null,
        TransformBase? leftHandTracker = null,
        TransformBase? rightHandTracker = null,
        TransformBase? leftFootTracker = null,
        TransformBase? rightFootTracker = null);

    /// <summary>
    /// Calibrates all humanoid targets from one captured pose snapshot.
    /// </summary>
    VrCalibrationResult CalibrateSnapshot(
        object solver,
        object? settings,
        VrCalibrationPose[] poses,
        float headTiltToleranceDegrees);
}
