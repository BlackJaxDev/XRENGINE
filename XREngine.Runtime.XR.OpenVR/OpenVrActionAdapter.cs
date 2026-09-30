using OpenVR.NET;
using OpenVR.NET.Devices;
using OpenVR.NET.Manifest;
using Valve.VR;
using XREngine.Input;

namespace XREngine;

internal sealed class OpenVrActionAdapter(RuntimeOpenVrActionDescriptor descriptor) : IAction
{
    public string Path => descriptor.Path;
    public Enum Name => descriptor.Name;
    public RuntimeOpenVrActionDescriptor Descriptor => descriptor;

    public OpenVR.NET.Input.Action CreateAction(VR vr, Controller? device)
    {
        ulong handle = 0;
        EVRInputError error = Valve.VR.OpenVR.Input.GetActionHandle(Path, ref handle);
        if (error != EVRInputError.None)
            vr.Events.Log($"Could not get handle for action {descriptor.Category}/{descriptor.Name}",
                EventType.CoundntFetchActionHandle, error);
        ulong deviceHandle = device?.Handle ?? Valve.VR.OpenVR.k_ulInvalidActionHandle;
        return descriptor.Type switch
        {
            RuntimeOpenVrActionType.Boolean => new OpenVR.NET.Input.BooleanAction { SourceHandle = handle, DeviceHandle = deviceHandle },
            RuntimeOpenVrActionType.Scalar => new OpenVR.NET.Input.ScalarAction { SourceHandle = handle, DeviceHandle = deviceHandle },
            RuntimeOpenVrActionType.Vector2 => new OpenVR.NET.Input.Vector2Action { SourceHandle = handle, DeviceHandle = deviceHandle },
            RuntimeOpenVrActionType.Vector3 => new OpenVR.NET.Input.Vector3Action { SourceHandle = handle, DeviceHandle = deviceHandle },
            RuntimeOpenVrActionType.Vibration => new OpenVR.NET.Input.HapticAction { SourceHandle = handle, DeviceHandle = deviceHandle },
            RuntimeOpenVrActionType.Pose => new OpenVR.NET.Input.PoseAction { SourceHandle = handle, DeviceHandle = deviceHandle, VR = vr },
            RuntimeOpenVrActionType.LeftHandSkeleton or RuntimeOpenVrActionType.RightHandSkeleton =>
                new OpenVR.NET.Input.HandSkeletonAction { SourceHandle = handle, DeviceHandle = deviceHandle },
            _ => throw new InvalidOperationException($"Unsupported OpenVR action type {descriptor.Type}."),
        };
    }
}
