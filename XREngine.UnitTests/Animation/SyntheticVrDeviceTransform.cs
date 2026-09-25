using System.Numerics;
using OpenVR.NET.Devices;
using XREngine.Data.Components.Scene;

namespace XREngine.UnitTests.Animation;

/// <summary>
/// A deterministic tracked pose for calibration tests, independent of a running VR service.
/// </summary>
public sealed class SyntheticVrDeviceTransform : VRDeviceTransformBase
{
    private bool _connected = true;
    private bool _positionValid = true;
    private bool _orientationValid = true;
    private long _timestamp;
    private Matrix4x4 _pose = Matrix4x4.Identity;

    public SyntheticVrDeviceTransform(string identity, string role, uint syntheticDeviceIndex)
    {
        Identity = identity;
        Role = role;
        SyntheticDeviceIndex = syntheticDeviceIndex;
    }

    /// <summary>Stable physical identity, independent of the current body role.</summary>
    public string Identity { get; }

    /// <summary>The semantic calibration slot occupied by this device.</summary>
    public string Role { get; }

    /// <summary>A stable identity for tests; no OpenVR device is allocated.</summary>
    public uint SyntheticDeviceIndex { get; }

    public bool Connected
    {
        get => _connected;
        set => SetField(ref _connected, value);
    }

    public bool PositionValid
    {
        get => _positionValid;
        set => SetField(ref _positionValid, value);
    }

    public bool OrientationValid
    {
        get => _orientationValid;
        set => SetField(ref _orientationValid, value);
    }

    /// <summary>A caller supplied sample timestamp in monotonic test ticks.</summary>
    public long Timestamp
    {
        get => _timestamp;
        set => SetField(ref _timestamp, value);
    }

    public Matrix4x4 Pose
    {
        get => _pose;
        set
        {
            if (SetField(ref _pose, value))
                MarkLocalModified();
        }
    }

    /// <summary>Whether this sample is suitable for role assignment; the flags do not alter its matrix.</summary>
    public bool PoseCurrentlyUsable => Connected && PositionValid && OrientationValid;

    public override VrDevice? Device => null;

    public void SetPose(Vector3 position, Quaternion orientation, long timestamp = 0)
    {
        Pose = Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(position);
        Timestamp = timestamp;
    }

    protected override Matrix4x4 CreateLocalMatrix() => Pose;
}
