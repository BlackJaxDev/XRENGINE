using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Core;
using XREngine.Extensions;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class EngineRuntimeVrStateServices : IRuntimeVrStateServices
{
    public event Action? FrameAdvanced
    {
        add
        {
            if (value is not null)
                Engine.Time.Timer.PreUpdateFrame += value;
        }
        remove
        {
            if (value is not null)
                Engine.Time.Timer.PreUpdateFrame -= value;
        }
    }

    public event Action? RecalcMatrixOnDraw
    {
        add
        {
            if (value is not null)
                Engine.VRState.RecalcMatrixOnDraw += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.RecalcMatrixOnDraw -= value;
        }
    }

    public event Action<float>? IPDScalarChanged
    {
        add
        {
            if (value is not null)
                Engine.VRState.IPDScalarChanged += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.IPDScalarChanged -= value;
        }
    }

    public event Action<float>? RealWorldHeightChanged
    {
        add
        {
            if (value is not null)
                Engine.VRState.RealWorldHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.RealWorldHeightChanged -= value;
        }
    }

    public event Action<float>? DesiredAvatarHeightChanged
    {
        add
        {
            if (value is not null)
                Engine.VRState.DesiredAvatarHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.DesiredAvatarHeightChanged -= value;
        }
    }

    public event Action<float>? ModelHeightChanged
    {
        add
        {
            if (value is not null)
                Engine.VRState.ModelHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.ModelHeightChanged -= value;
        }
    }

    public event Action<VrDevice>? DeviceDetected
    {
        add
        {
            if (value is not null)
                Engine.VRState.OpenVRApi.DeviceDetected += value;
        }
        remove
        {
            if (value is not null)
                Engine.VRState.OpenVRApi.DeviceDetected -= value;
        }
    }

    public RuntimeVrRuntimeKind ActiveRuntime
        => Engine.VRState.ActiveRuntime switch
        {
            Engine.VRState.VRRuntime.OpenVR => RuntimeVrRuntimeKind.OpenVR,
            Engine.VRState.VRRuntime.OpenXR => RuntimeVrRuntimeKind.OpenXR,
            _ => RuntimeVrRuntimeKind.None,
        };

    public bool IsOpenXRActive
        => Engine.VRState.IsOpenXRActive;

    public bool IsInVR
        => Engine.VRState.IsInVR;

    public object? CalibrationSettings
        => Engine.VRState.CalibrationSettings;

    public float RealWorldIPD
        => Engine.VRState.RealWorldIPD;

    public float ScaledIPD
        => Engine.VRState.ScaledIPD;

    public float ModelToRealWorldHeightRatio
        => Engine.VRState.ModelToRealWorldHeightRatio;

    public VrDevice? Headset
        => Engine.VRState.OpenVRApi.Headset;

    public VrDevice? LeftController
        => Engine.VRState.OpenVRApi.LeftController;

    public VrDevice? RightController
        => Engine.VRState.OpenVRApi.RightController;

    public IEnumerable<VrDevice> TrackedDevices
        => Engine.VRState.OpenVRApi.TrackedDevices;

    public bool IsGenericTracker(uint deviceIndex)
        => Engine.VRState.OpenVRApi.CVR is { } cvr &&
            cvr.GetTrackedDeviceClass(deviceIndex) == ETrackedDeviceClass.GenericTracker;

    public bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out OpenXRAPI? openXrApi))
            return openXrApi.TryGetHeadLocalPose(MapPoseTiming(openXrApi, timing), out pose);

        if ((timing == RuntimeVrPoseTiming.Recalc ? Headset?.RenderDeviceToAbsoluteTrackingMatrix : Headset?.DeviceToAbsoluteTrackingMatrix) is Matrix4x4 matrix)
        {
            pose = matrix;
            return true;
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out OpenXRAPI? openXrApi))
            return openXrApi.TryGetControllerLocalPose(leftHand, MapPoseTiming(openXrApi, timing), out pose);

        VrDevice? controller = leftHand ? LeftController : RightController;
        if ((timing == RuntimeVrPoseTiming.Recalc ? controller?.RenderDeviceToAbsoluteTrackingMatrix : controller?.DeviceToAbsoluteTrackingMatrix) is Matrix4x4 matrix)
        {
            pose = matrix;
            return true;
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out OpenXRAPI? openXrApi) && !string.IsNullOrWhiteSpace(trackerUserPath))
            return openXrApi.TryGetTrackerLocalPose(trackerUserPath, MapPoseTiming(openXrApi, timing), out pose);

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out OpenXRAPI? openXrApi))
        {
            if (openXrApi.TryGetHeadLocalPose(out Matrix4x4 headLocal) &&
                openXrApi.TryGetEyeLocalPose(leftEye, out Matrix4x4 eyeLocal) &&
                Matrix4x4.Invert(headLocal, out Matrix4x4 inverseHead))
            {
                pose = inverseHead * eyeLocal;
                return true;
            }

            pose = Matrix4x4.Identity;
            return false;
        }

        if (Engine.VRState.IsInVR && Engine.VRState.OpenVRApi.CVR is { } cvr)
        {
            EVREye eye = leftEye ? EVREye.Eye_Left : EVREye.Eye_Right;
            pose = ToNumerics(cvr.GetEyeToHeadTransform(eye)).Transposed().Inverted();
            return true;
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    private static Matrix4x4 ToNumerics(HmdMatrix34_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            0, 0, 0, 1);

    private static bool TryGetOpenXr([NotNullWhen(true)] out OpenXRAPI? openXrApi)
    {
        openXrApi = Engine.VRState.IsOpenXRActive ? Engine.VRState.OpenXRApi : null;
        return openXrApi is not null;
    }

    private static OpenXRAPI.OpenXrPoseTiming MapPoseTiming(OpenXRAPI openXrApi, RuntimeVrPoseTiming timing)
        => timing == RuntimeVrPoseTiming.Recalc
            ? openXrApi.PoseTimingForRecalc
            : OpenXRAPI.OpenXrPoseTiming.Predicted;
}