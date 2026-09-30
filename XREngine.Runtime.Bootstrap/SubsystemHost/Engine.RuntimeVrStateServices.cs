using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine.Core;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine;

internal sealed class EngineRuntimeVrStateServices : IRuntimeVrStateServices, IDisposable
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

    private readonly object _deviceEventSync = new();
    private Action<RuntimeVrDeviceInfo>? _deviceDetected;
    private bool _deviceHooked;

    public event Action<RuntimeVrDeviceInfo>? DeviceDetected
    {
        add
        {
            if (value is null)
                return;
            lock (_deviceEventSync)
            {
                if (!_deviceHooked)
                {
                    OpenVrDeviceBackend.DeviceDetected += ForwardDeviceDetected;
                    _deviceHooked = true;
                }
                _deviceDetected += value;
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_deviceEventSync)
            {
                _deviceDetected -= value;
                if (_deviceHooked && _deviceDetected is null)
                {
                    OpenVrDeviceBackend.DeviceDetected -= ForwardDeviceDetected;
                    _deviceHooked = false;
                }
            }
        }
    }

    private void ForwardDeviceDetected(RuntimeVrDeviceInfo device)
        => _deviceDetected?.Invoke(device);

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
        => EngineVrLifecycle.CalibrationSettings;

    public string? GetControllerInteractionProfile(bool leftHand) => RuntimeEngine.VRState.OpenXRApi?.GetControllerInteractionProfile(leftHand);

    public float CalibrationHeadTiltToleranceDegrees => EngineVrLifecycle.CalibrationSettings.HeadTiltToleranceDegrees;

    public UserSettings? PlayerSettings => Engine.UserSettings;

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

    public RuntimeVrDeviceInfo? Headset
        => OpenVrDeviceBackend.Headset;

    public RuntimeVrDeviceInfo? LeftController
        => OpenVrDeviceBackend.LeftController;

    public RuntimeVrDeviceInfo? RightController
        => OpenVrDeviceBackend.RightController;

    public IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices => OpenVrDeviceBackend.GetTrackedDevices();

    public string[] GetKnownOpenXrTrackerUserPaths()
        => TryGetOpenXr(out IOpenXrRuntime? openXrApi)
            ? openXrApi.GetKnownTrackerUserPaths()
            : [];

    public RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers()
        => TryGetOpenXr(out IOpenXrRuntime? openXrApi)
            ? openXrApi.GetKnownTrackers()
            : [];

    public bool IsGenericTracker(uint deviceIndex)
        => OpenVrDeviceBackend.IsGenericTracker(deviceIndex);

    public bool TryCopyTrackingSnapshot(Span<RuntimeVrTrackerPose> trackers, out RuntimeVrTrackingSnapshot snapshot, out int trackerCount)
    {
        if (TryGetOpenXr(out IOpenXrRuntime? openXrApi))
            return openXrApi.TryCopyTrackingSnapshot(trackers, out snapshot, out trackerCount);
        snapshot = default;
        trackerCount = 0;
        return false;
    }

    public bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
        => OpenVrDeviceBackend.TryGetDeviceLocalPose(deviceIndex, timing, out pose);

    public bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out IOpenXrRuntime? openXrApi))
            return openXrApi.TryGetHeadLocalPose(MapPoseTiming(openXrApi, timing), out pose);

        if (IsOpenXRActive)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        if (Headset is { } headset && TryGetDeviceLocalPose(headset.DeviceIndex, timing, out Matrix4x4 matrix))
        {
            pose = matrix;
            return true;
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out IOpenXrRuntime? openXrApi))
            return openXrApi.TryGetControllerLocalPose(leftHand, MapPoseTiming(openXrApi, timing), out pose);

        if (IsOpenXRActive)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        RuntimeVrDeviceInfo? controller = leftHand ? LeftController : RightController;
        if (controller is { } tracked && TryGetDeviceLocalPose(tracked.DeviceIndex, timing, out Matrix4x4 matrix))
        {
            pose = matrix;
            return true;
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (TryGetOpenXr(out IOpenXrRuntime? openXrApi) && !string.IsNullOrWhiteSpace(trackerUserPath))
            return openXrApi.TryGetTrackerLocalPose(trackerUserPath, MapPoseTiming(openXrApi, timing), out pose);

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
    {
        if (RuntimeEngine.VRState.EmulatedRenderActive)
        {
            float halfIpd = RuntimeEngine.VRState.RealWorldIPD * 0.5f;
            pose = Matrix4x4.CreateTranslation(leftEye ? -halfIpd : halfIpd, 0f, 0f);
            return true;
        }
        if (TryGetOpenXr(out IOpenXrRuntime? openXrApi))
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

        if (RuntimeEngine.VRState.IsInVR)
            return OpenVrDeviceBackend.TryGetHeadToEyeLocalPose(leftEye, out pose);

        pose = Matrix4x4.Identity;
        return false;
    }

    public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection)
    {
        if (!RuntimeEngine.VRState.IsOpenVRActive)
        {
            projection = Matrix4x4.Identity;
            return false;
        }
        return OpenVrDeviceBackend.TryGetEyeProjectionMatrix(leftEye, nearPlane, farPlane, out projection);
    }

    private static bool TryGetOpenXr([NotNullWhen(true)] out IOpenXrRuntime? openXrApi)
    {
        openXrApi = RuntimeEngine.VRState.IsOpenXRActive ? RuntimeEngine.VRState.OpenXRApi : null;
        return openXrApi is not null;
    }

    private static RuntimeOpenXrPoseTiming MapPoseTiming(IOpenXrRuntime openXrApi, RuntimeVrPoseTiming timing)
        => timing == RuntimeVrPoseTiming.Late || timing == RuntimeVrPoseTiming.Recalc
            ? RuntimeOpenXrPoseTiming.Late
            : RuntimeOpenXrPoseTiming.Predicted;

    public void Dispose()
    {
        lock (_deviceEventSync)
        {
            if (_deviceHooked)
                OpenVrDeviceBackend.DeviceDetected -= ForwardDeviceDetected;
            _deviceDetected = null;
            _deviceHooked = false;
        }
    }
}
