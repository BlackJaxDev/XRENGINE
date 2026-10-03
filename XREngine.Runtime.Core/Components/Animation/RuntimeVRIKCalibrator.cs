using System;
using System.Reflection;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Core;

public static class RuntimeVRIKCalibrator
{
    private const string CalibrationSettingsTypeName = "XREngine.Components.Animation.VRIKCalibrationSettings";
    private const string VRIKCalibratorTypeName = "XREngine.Components.Animation.VRIKCalibrator";
    private const string VRIKSolverComponentTypeName = "XREngine.Components.Animation.VRIKSolverComponent";

    private static MethodInfo? _calibrateMethod;


    public static VrCalibrationResult Calibrate(
        object solver,
        object? settings,
        TransformBase? headTracker,
        TransformBase? bodyTracker = null,
        TransformBase? leftHandTracker = null,
        TransformBase? rightHandTracker = null,
        TransformBase? leftFootTracker = null,
        TransformBase? rightFootTracker = null)
    {
        if (settings is null)
            return VrCalibrationResult.Failure("Calibration settings are unavailable.");
        MethodInfo? method = ResolveCalibrateMethod();
        if (method is null)
            return VrCalibrationResult.Failure("Unable to resolve the runtime VRIK calibrator.");

        return method.Invoke(null,
        [
            solver,
            settings,
            headTracker,
            bodyTracker,
            leftHandTracker,
            rightHandTracker,
            leftFootTracker,
            rightFootTracker,
        ]) as VrCalibrationResult ?? VrCalibrationResult.Failure("The calibrator returned no result.");
    }

    /// <summary>Calibrates all humanoid sources from one copied tracking publication.</summary>
    public static VrCalibrationResult CalibrateSnapshot(object solver, object? settings, VrCalibrationPose[] poses, float headTiltToleranceDegrees)
    {
        if (settings is null)
            return VrCalibrationResult.Failure("Calibration settings are unavailable.");
        Type? type = ResolveRuntimeType(VRIKCalibratorTypeName);
        MethodInfo? method = type?.GetMethod("TryCalibrateSnapshot", BindingFlags.Public | BindingFlags.Static);
        if (method is null)
            return VrCalibrationResult.Failure("The snapshot calibrator is unavailable.");
        try
        {
            return method.Invoke(null, [solver, settings, poses, headTiltToleranceDegrees]) as VrCalibrationResult
                ?? VrCalibrationResult.Failure("The snapshot calibrator returned no result.");
        }
        catch (TargetInvocationException exception)
        {
            return VrCalibrationResult.Failure(exception.InnerException?.Message ?? exception.Message);
        }
    }

    private static MethodInfo? ResolveCalibrateMethod()
    {
        if (_calibrateMethod is not null)
            return _calibrateMethod;

        Type? calibratorType = ResolveRuntimeType(VRIKCalibratorTypeName);
        Type? solverType = ResolveRuntimeType(VRIKSolverComponentTypeName);
        Type? calibrationSettingsType = ResolveRuntimeType(CalibrationSettingsTypeName);
        if (calibratorType is null || solverType is null || calibrationSettingsType is null)
            return null;

        _calibrateMethod = calibratorType.GetMethod(
            name: "TryCalibrate",
            bindingAttr: BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types:
            [
                solverType,
                calibrationSettingsType,
                typeof(TransformBase),
                typeof(TransformBase),
                typeof(TransformBase),
                typeof(TransformBase),
                typeof(TransformBase),
                typeof(TransformBase),
            ],
            modifiers: null);

        return _calibrateMethod;
    }

    private static Type? ResolveRuntimeType(string typeName)
    {
        Type? resolved = Type.GetType($"{typeName}, XREngine.Runtime.AnimationIntegration")
            ?? Type.GetType($"{typeName}, XREngine.Animation")
            ?? Type.GetType($"{typeName}, XRENGINE");
        if (resolved is not null)
        {
            XREngine.Data.Runtime.AotParity.AotParityDiagnostics.Report(
                resolved,
                XREngine.Data.Runtime.AotParity.EAotParityCategory.TypeResolutionScan,
                $"{nameof(RuntimeVRIKCalibrator)}.{nameof(ResolveRuntimeType)}",
                "Route VR IK calibration through a registered host service instead of resolving the calibrator type by name.");
        }

        return resolved;
    }
}
