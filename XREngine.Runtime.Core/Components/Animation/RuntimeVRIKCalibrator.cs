using System;
using System.Reflection;
using XREngine.Scene.Transforms;

namespace XREngine.Core;

public static class RuntimeVRIKCalibrator
{
    private const string CalibrationSettingsTypeName = "XREngine.Components.Animation.VRIKCalibrationSettings";
    private const string VRIKCalibratorTypeName = "XREngine.Components.Animation.VRIKCalibrator";
    private const string VRIKSolverComponentTypeName = "XREngine.Components.Animation.VRIKSolverComponent";

    private static MethodInfo? _calibrateMethod;

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
        MethodInfo method = ResolveCalibrateMethod()
            ?? throw new InvalidOperationException("Unable to resolve the runtime VRIK calibrator.");

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
        ]);
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
            name: "Calibrate",
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
        => Type.GetType($"{typeName}, XREngine.Runtime.AnimationIntegration")
        ?? Type.GetType($"{typeName}, XREngine.Animation")
        ?? Type.GetType($"{typeName}, XRENGINE");
}