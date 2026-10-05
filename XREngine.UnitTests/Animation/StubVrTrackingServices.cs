using System.Numerics;
using XREngine.Input;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Animation;

/// <summary>Native-free tracker discovery service for scene lifecycle regressions.</summary>
internal sealed class StubVrTrackingServices : IRuntimeVrStateServices
{
    public RuntimeVrTrackerInfo[] Trackers { get; set; } = [];
    public RuntimeVrTrackerPose[] TrackerPoses { get; set; } = [];
    public RuntimeVrTrackingSnapshot Snapshot { get; set; }
    public bool SnapshotAvailable { get; set; }
    public UserSettings? PlayerSettings { get; set; }
    public event Action? FrameAdvanced { add { } remove { } }
    public event Action<RuntimeVrPoseTiming>? RecalcMatrixOnDraw { add { } remove { } }
    public event Action<float>? IPDScalarChanged { add { } remove { } }
    public event Action<float>? RealWorldHeightChanged { add { } remove { } }
    public event Action<float>? DesiredAvatarHeightChanged { add { } remove { } }
    public event Action<float>? ModelHeightChanged { add { } remove { } }
    public event Action<RuntimeVrDeviceInfo>? DeviceDetected { add { } remove { } }
    public RuntimeVrRuntimeKind ActiveRuntime => RuntimeVrRuntimeKind.OpenXR;
    public bool IsOpenXRActive => true;
    public bool IsInVR => true;
    public object? CalibrationSettings { get; set; }
    public float CalibrationHeadTiltToleranceDegrees { get; set; } = 10.0f;
    public float RealWorldIPD => 0.064f;
    public float ScaledIPD => RealWorldIPD;
    public float ModelToRealWorldHeightRatio => 1.0f;
    public float ModelHeight { get; set; } = 1.8f;
    public RuntimeVrDeviceInfo? Headset => null;
    public RuntimeVrDeviceInfo? LeftController => null;
    public RuntimeVrDeviceInfo? RightController => null;
    public IReadOnlyList<RuntimeVrDeviceInfo> TrackedDevices => Array.Empty<RuntimeVrDeviceInfo>();
    public string[] GetKnownOpenXrTrackerUserPaths() => Trackers.Select(t => t.UserPath).ToArray();
    public RuntimeVrTrackerInfo[] GetKnownOpenXrTrackers() => Trackers;
    public bool IsGenericTracker(uint deviceIndex) => false;
    public bool TryGetDeviceLocalPose(uint deviceIndex, RuntimeVrPoseTiming timing, out Matrix4x4 pose) { pose = default; return false; }
    public bool TryGetHeadLocalPose(RuntimeVrPoseTiming timing, out Matrix4x4 pose) { pose = Snapshot.HeadPose; return SnapshotAvailable && Snapshot.HeadValid; }
    public bool TryGetControllerLocalPose(bool leftHand, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        pose = leftHand ? Snapshot.LeftControllerPose : Snapshot.RightControllerPose;
        return SnapshotAvailable && (leftHand ? Snapshot.LeftControllerValid : Snapshot.RightControllerValid);
    }
    public bool TryGetTrackerLocalPose(string trackerUserPath, RuntimeVrPoseTiming timing, out Matrix4x4 pose)
    {
        foreach (var sample in TrackerPoses)
            if (sample.Info.UserPath == trackerUserPath)
            {
                pose = sample.LocalPose;
                return SnapshotAvailable && sample.Info.PoseCurrentlyUsable;
            }
        pose = default;
        return false;
    }
    public bool TryCopyTrackingSnapshot(Span<RuntimeVrTrackerPose> trackers, out RuntimeVrTrackingSnapshot snapshot, out int trackerCount)
    {
        snapshot = Snapshot;
        trackerCount = TrackerPoses.Length;
        if (!SnapshotAvailable || trackers.Length < trackerCount)
            return false;
        TrackerPoses.CopyTo(trackers);
        return true;
    }
    public bool TryGetHeadToEyeLocalPose(bool leftEye, out Matrix4x4 pose) { pose = default; return false; }
    public bool TryGetEyeProjectionMatrix(bool leftEye, float nearPlane, float farPlane, out Matrix4x4 projection) { projection = default; return false; }
}
