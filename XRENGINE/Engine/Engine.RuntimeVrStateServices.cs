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
    public EngineRuntimeVrStateServices()
        => RuntimeEngine.VRState.LifecycleServices = new EngineRuntimeVrLifecycleServices();

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

    public event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.RecalcMatrixOnDraw += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.RecalcMatrixOnDraw -= value;
        }
    }

    public event Action<float>? IPDScalarChanged
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.IPDScalarChanged += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.IPDScalarChanged -= value;
        }
    }

    public event Action<float>? RealWorldHeightChanged
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.RealWorldHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.RealWorldHeightChanged -= value;
        }
    }

    public event Action<float>? DesiredAvatarHeightChanged
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.DesiredAvatarHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.DesiredAvatarHeightChanged -= value;
        }
    }

    public event Action<float>? ModelHeightChanged
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.ModelHeightChanged += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.ModelHeightChanged -= value;
        }
    }

    public event Action<VrDevice>? DeviceDetected
    {
        add
        {
            if (value is not null)
                RuntimeEngine.VRState.OpenVRApi.DeviceDetected += value;
        }
        remove
        {
            if (value is not null)
                RuntimeEngine.VRState.OpenVRApi.DeviceDetected -= value;
        }
    }

    public RuntimeVrRuntimeKind ActiveRuntime
        => RuntimeEngine.VRState.ActiveRuntime switch
        {
            RuntimeVrState.VRRuntime.OpenVR => RuntimeVrRuntimeKind.OpenVR,
            RuntimeVrState.VRRuntime.OpenXR => RuntimeVrRuntimeKind.OpenXR,
            _ => RuntimeVrRuntimeKind.None,
        };

    public bool IsOpenXRActive
        => RuntimeEngine.VRState.IsOpenXRActive;

    public bool IsInVR
        => RuntimeEngine.VRState.IsInVR;

    public object? CalibrationSettings
        => RuntimeEngine.VRState.CalibrationSettings;

    public float RealWorldIPD
        => RuntimeEngine.VRState.RealWorldIPD;

    public float ScaledIPD
        => RuntimeEngine.VRState.ScaledIPD;

    public float ModelToRealWorldHeightRatio
        => RuntimeEngine.VRState.ModelToRealWorldHeightRatio;

    public float ModelHeight
    {
        get => RuntimeEngine.VRState.ModelHeight;
        set => RuntimeEngine.VRState.ModelHeight = value;
    }

    public VrDevice? Headset
        => RuntimeEngine.VRState.OpenVRApi.Headset;

    public VrDevice? LeftController
        => RuntimeEngine.VRState.OpenVRApi.LeftController;

    public VrDevice? RightController
        => RuntimeEngine.VRState.OpenVRApi.RightController;

    public IEnumerable<VrDevice> TrackedDevices
        => RuntimeEngine.VRState.OpenVRApi.TrackedDevices;

    public string[] GetKnownOpenXrTrackerUserPaths()
        => TryGetOpenXr(out OpenXRAPI? openXrApi)
            ? openXrApi.GetKnownTrackerUserPaths()
            : [];

    public RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers()
        => TryGetOpenXr(out OpenXRAPI? openXrApi)
            ? openXrApi.GetKnownTrackers()
            : [];

    public bool IsGenericTracker(uint deviceIndex)
        => RuntimeEngine.VRState.OpenVRApi.CVR is { } cvr &&
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
                pose = eyeLocal * inverseHead;
                return true;
            }

            pose = Matrix4x4.Identity;
            return false;
        }

        if (RuntimeEngine.VRState.IsInVR && RuntimeEngine.VRState.OpenVRApi.CVR is { } cvr)
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
        openXrApi = RuntimeEngine.VRState.IsOpenXRActive ? RuntimeEngine.VRState.OpenXRApi : null;
        return openXrApi is not null;
    }

    private static OpenXRAPI.OpenXrPoseTiming MapPoseTiming(OpenXRAPI openXrApi, RuntimeVrPoseTiming timing)
        => timing == RuntimeVrPoseTiming.Late || timing == RuntimeVrPoseTiming.Recalc
            ? OpenXRAPI.OpenXrPoseTiming.Late
            : OpenXRAPI.OpenXrPoseTiming.Predicted;
}
