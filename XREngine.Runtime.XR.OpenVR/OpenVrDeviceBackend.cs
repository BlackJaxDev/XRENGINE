using System.Numerics;
using OpenVR.NET;
using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Extensions;
using XREngine.Input;

namespace XREngine;

/// <summary>Copies tracked-device identity and poses out of the OpenVR API.</summary>
public static class OpenVrDeviceBackend
{
    private static readonly object EventSync = new();
    private static Action<RuntimeVrDeviceInfo>? _deviceDetected;

    public static event Action<RuntimeVrDeviceInfo>? DeviceDetected
    {
        add
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                if (_deviceDetected is null)
                    OpenVrRuntimeBackend.Api.DeviceDetected += OnDeviceDetected;
                _deviceDetected += value;
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (EventSync)
            {
                _deviceDetected -= value;
                if (_deviceDetected is null && OpenVrRuntimeBackend.ApiIfCreated is { } vr)
                    vr.DeviceDetected -= OnDeviceDetected;
            }
        }
    }

    private static void OnDeviceDetected(VrDevice device)
        => _deviceDetected?.Invoke(ToDeviceInfo(device));

    public static RuntimeVrDeviceInfo? Headset
        => ToDeviceInfoOrNull(OpenVrRuntimeBackend.ApiIfCreated?.Headset);

    public static RuntimeVrDeviceInfo? LeftController
        => ToDeviceInfoOrNull(OpenVrRuntimeBackend.ApiIfCreated?.LeftController);

    public static RuntimeVrDeviceInfo? RightController
        => ToDeviceInfoOrNull(OpenVrRuntimeBackend.ApiIfCreated?.RightController);

    public static IReadOnlyList<RuntimeVrDeviceInfo> GetTrackedDevices()
    {
        if (OpenVrRuntimeBackend.ApiIfCreated is not { } vr)
            return Array.Empty<RuntimeVrDeviceInfo>();

        List<RuntimeVrDeviceInfo> devices = [];
        foreach (VrDevice device in vr.TrackedDevices)
            devices.Add(ToDeviceInfo(device));
        return devices;
    }

    private static RuntimeVrDeviceInfo? ToDeviceInfoOrNull(VrDevice? device)
        => device is null ? null : ToDeviceInfo(device);

    private static RuntimeVrDeviceInfo ToDeviceInfo(VrDevice device)
    {
        ETrackedDeviceClass deviceClass = device.VR.CVR?.GetTrackedDeviceClass(device.DeviceIndex)
            ?? ETrackedDeviceClass.Invalid;
        return new RuntimeVrDeviceInfo(
            RuntimeVrRuntimeKind.OpenVR,
            device.DeviceIndex,
            (RuntimeVrDeviceClass)deviceClass,
            device.IsEnabled);
    }

    public static bool IsGenericTracker(uint deviceIndex)
        => OpenVrRuntimeBackend.Api.CVR is { } cvr &&
            cvr.GetTrackedDeviceClass(deviceIndex) == ETrackedDeviceClass.GenericTracker;

    public static bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        if (OpenVrRuntimeBackend.ApiIfCreated is { } vr)
        {
            foreach (VrDevice device in vr.TrackedDevices)
            {
                if (device.DeviceIndex != deviceIndex)
                    continue;

                pose = timing == RuntimeVrPoseTiming.Recalc
                    ? device.RenderDeviceToAbsoluteTrackingMatrix
                    : device.DeviceToAbsoluteTrackingMatrix;
                return true;
            }
        }

        pose = Matrix4x4.Identity;
        return false;
    }

    public static bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose)
    {
        if (OpenVrRuntimeBackend.ApiIfCreated?.CVR is not { } cvr)
        {
            pose = Matrix4x4.Identity;
            return false;
        }

        EVREye eye = leftEye ? EVREye.Eye_Left : EVREye.Eye_Right;
        pose = ToNumerics(cvr.GetEyeToHeadTransform(eye)).Transposed().Inverted();
        return true;
    }

    public static bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection)
    {
        if (OpenVrRuntimeBackend.ApiIfCreated?.CVR is not { } cvr)
        {
            projection = Matrix4x4.Identity;
            return false;
        }

        HmdMatrix44_t native = cvr.GetProjectionMatrix(
            leftEye ? EVREye.Eye_Left : EVREye.Eye_Right, nearPlane, farPlane);
        projection = new Matrix4x4(
            native.m0, native.m1, native.m2, native.m3,
            native.m4, native.m5, native.m6, native.m7,
            native.m8, native.m9, native.m10, native.m11,
            native.m12, native.m13, native.m14, native.m15).Transposed();
        return true;
    }

    private static Matrix4x4 ToNumerics(HmdMatrix34_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            0, 0, 0, 1);
}
