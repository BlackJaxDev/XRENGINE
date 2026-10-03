using OpenVR.NET;
using Valve.VR;

namespace XREngine;

/// <summary>Reads OpenVR headset calibration from the initialized native API.</summary>
internal sealed class OpenVrStateProvider : IRuntimeOpenVrStateProvider
{
    public static OpenVrStateProvider Instance { get; } = new();

    public float RealWorldIpd
    {
        get
        {
            VR? vr = OpenVrRuntimeBackend.ApiIfCreated;
            if (vr?.Headset is null || vr.CVR is null)
                return 0f;

            ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
            return vr.CVR.GetFloatTrackedDeviceProperty(
                vr.Headset.DeviceIndex,
                ETrackedDeviceProperty.Prop_UserIpdMeters_Float,
                ref error);
        }
    }

    public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out System.Numerics.Matrix4x4 projection)
        => OpenVrDeviceBackend.TryGetEyeProjectionMatrix(leftEye, nearPlane, farPlane, out projection);
}
