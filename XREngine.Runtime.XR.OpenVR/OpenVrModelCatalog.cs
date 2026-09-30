using System.Text;
using OpenVR.NET;
using OpenVR.NET.Devices;
using Valve.VR;

namespace XREngine;

/// <summary>Resolves OpenVR render-model names while retaining the utility runtime in the XR leaf.</summary>
public sealed class OpenVrModelCatalog
{
    private static readonly string[] TrackerRoleOrder =
    [
        "/user/vive_tracker_htcx/role/waist",
        "/user/vive_tracker_htcx/role/chest",
        "/user/vive_tracker_htcx/role/left_foot",
        "/user/vive_tracker_htcx/role/right_foot",
        "/user/vive_tracker_htcx/role/left_shoulder",
        "/user/vive_tracker_htcx/role/right_shoulder",
        "/user/vive_tracker_htcx/role/left_elbow",
        "/user/vive_tracker_htcx/role/right_elbow",
        "/user/vive_tracker_htcx/role/left_knee",
        "/user/vive_tracker_htcx/role/right_knee",
        "/user/vive_tracker_htcx/role/camera",
        "/user/vive_tracker_htcx/role/keyboard",
    ];

    private VR? _utilityRuntime;
    private bool _triedUtilityRuntime;

    public event Action? ModelsChanged;

    public string? LastFailure { get; private set; }

    public bool TryGetControllerModelName(bool leftHand, bool activeOpenVr, bool allowUtilityRuntime, out string? modelName)
    {
        modelName = null;
        if (activeOpenVr)
        {
            VrDevice? device = leftHand ? OpenVrRuntimeBackend.Api.LeftController : OpenVrRuntimeBackend.Api.RightController;
            modelName = device?.Model?.Name;
            if (!string.IsNullOrWhiteSpace(modelName))
                return true;
        }

        if (!TryGetSystem(activeOpenVr, allowUtilityRuntime, out CVRSystem? cvr))
            return false;
        ETrackedControllerRole role = leftHand ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
        uint deviceIndex = cvr.GetTrackedDeviceIndexForControllerRole(role);
        return deviceIndex != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid &&
            cvr.GetTrackedDeviceClass(deviceIndex) == ETrackedDeviceClass.Controller &&
            TryGetModelName(cvr, deviceIndex, out modelName);
    }

    public bool TryGetTrackerModelName(
        uint? explicitDeviceIndex,
        string? openXrTrackerUserPath,
        bool activeOpenVr,
        bool allowUtilityRuntime,
        out uint deviceIndex,
        out string? modelName)
    {
        deviceIndex = 0;
        modelName = null;
        if (activeOpenVr && explicitDeviceIndex is uint trackedIndex)
        {
            foreach (VrDevice device in OpenVrRuntimeBackend.Api.TrackedDevices)
            {
                if (device.DeviceIndex != trackedIndex ||
                    OpenVrRuntimeBackend.Api.CVR.GetTrackedDeviceClass(trackedIndex) != ETrackedDeviceClass.GenericTracker)
                    continue;
                deviceIndex = trackedIndex;
                modelName = device.Model?.Name;
                if (!string.IsNullOrWhiteSpace(modelName))
                    return true;
            }
        }

        if (!TryGetSystem(activeOpenVr, allowUtilityRuntime, out CVRSystem? cvr))
            return false;

        if (explicitDeviceIndex is uint explicitIndex &&
            cvr.GetTrackedDeviceClass(explicitIndex) == ETrackedDeviceClass.GenericTracker &&
            TryGetModelName(cvr, explicitIndex, out modelName))
        {
            deviceIndex = explicitIndex;
            return true;
        }

        if (string.IsNullOrWhiteSpace(openXrTrackerUserPath))
            return false;

        Span<uint> indices = stackalloc uint[(int)Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];
        int count = 0;
        for (uint i = 0; i < Valve.VR.OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (cvr.GetTrackedDeviceClass(i) == ETrackedDeviceClass.GenericTracker)
                indices[count++] = i;
        }
        if (count == 0)
            return false;

        deviceIndex = SelectTrackerIndex(openXrTrackerUserPath, indices[..count]);
        return TryGetModelName(cvr, deviceIndex, out modelName);
    }

    public bool HasSystem(bool activeOpenVr, bool allowUtilityRuntime)
        => TryGetSystem(activeOpenVr, allowUtilityRuntime, out _);

    private bool TryGetSystem(bool activeOpenVr, bool allowUtilityRuntime, out CVRSystem? cvr)
    {
        cvr = null;
        if (activeOpenVr &&
            OpenVrRuntimeBackend.Api.State.HasFlag(VrState.OK) &&
            OpenVrRuntimeBackend.Api.CVR is { } active)
        {
            cvr = active;
            return true;
        }
        if (!allowUtilityRuntime)
            return false;
        if (_utilityRuntime is { State: var state } && state.HasFlag(VrState.OK) && _utilityRuntime.CVR is { } existing)
        {
            cvr = existing;
            return true;
        }
        if (_triedUtilityRuntime)
            return false;

        _triedUtilityRuntime = true;
        try
        {
            _utilityRuntime = new VR();
            if (!_utilityRuntime.TryStart(EVRApplicationType.VRApplication_Utility))
            {
                LastFailure = "OpenVR utility initialization failed";
                return false;
            }
        }
        catch (Exception ex)
        {
            LastFailure = $"OpenVR utility initialization failed: {ex.Message}";
            return false;
        }

        cvr = _utilityRuntime.CVR;
        ModelsChanged?.Invoke();
        return cvr is not null;
    }

    private static bool TryGetModelName(CVRSystem cvr, uint deviceIndex, out string? modelName)
    {
        modelName = null;
        ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
        StringBuilder builder = new(256);
        uint length = cvr.GetStringTrackedDeviceProperty(
            deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String,
            builder, (uint)builder.Capacity, ref error);
        if (error == ETrackedPropertyError.TrackedProp_BufferTooSmall)
        {
            builder.EnsureCapacity((int)length);
            error = ETrackedPropertyError.TrackedProp_Success;
            cvr.GetStringTrackedDeviceProperty(
                deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String,
                builder, length, ref error);
        }
        if (error != ETrackedPropertyError.TrackedProp_Success)
            return false;
        modelName = builder.ToString();
        return !string.IsNullOrWhiteSpace(modelName);
    }

    private static uint SelectTrackerIndex(string path, ReadOnlySpan<uint> indices)
    {
        if (indices.Length == 1)
            return indices[0];
        for (int i = 0; i < TrackerRoleOrder.Length; i++)
        {
            if (string.Equals(TrackerRoleOrder[i], path, StringComparison.Ordinal))
                return indices[Math.Min(i, indices.Length - 1)];
        }
        int hash = StringComparer.Ordinal.GetHashCode(path) & int.MaxValue;
        return indices[hash % indices.Length];
    }
}
