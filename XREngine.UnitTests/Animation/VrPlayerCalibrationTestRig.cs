using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Runs the actual player state machine with real device transforms and the production humanoid/solver.</summary>
internal sealed class VrPlayerCalibrationTestRig : IDisposable
{
    private readonly IRuntimeVrStateServices _previousState = RuntimeVrStateServices.Current;
    private readonly IRuntimeVrInputServices _previousInput = RuntimeVrInputServices.Current;
    public SyntheticVrCalibrationRig Avatar { get; } = new();
    public StubVrTrackingServices State { get; } = new();
    public StubVrCalibrationInputServices Input { get; } = new();
    public VRPlayerCharacterComponent Player { get; }
    public VRHeadsetTransform Head { get; } = new();
    public VRControllerTransform Left { get; } = new() { LeftHand = true };
    public VRControllerTransform Right { get; } = new() { LeftHand = false };

    public VrPlayerCalibrationTestRig()
    {
        RuntimeVrStateServices.Current = State;
        RuntimeVrInputServices.Current = Input;
        State.CalibrationSettings = Avatar.Settings;
        Avatar.Humanoid.TryGetCanonicalBodyMeasurements(Vector3.Zero, out AvatarBodyMeasurements measurements);
        State.PlayerSettings = new UserSettings { PlayerHeight = measurements.EyeHeight / 0.936f };
        State.SnapshotAvailable = true;
        State.Snapshot = new(1, 1, 1_000_000_000,
            Avatar.Head.Pose, true, Avatar.LeftHand.Pose, true, Avatar.RightHand.Pose, true);
        _ = new SceneNode(Avatar.Playspace, "VRHeadsetNode", Head);
        _ = new SceneNode(Avatar.Playspace, "VRLeftControllerNode", Left);
        _ = new SceneNode(Avatar.Playspace, "VRRightControllerNode", Right);
        VRTrackerCollectionComponent trackers = Avatar.SceneRoot.AddComponent<VRTrackerCollectionComponent>()!;
        VRHeightScaleComponent scale = Avatar.AvatarRoot.AddComponent<VRHeightScaleComponent>()!;
        scale.HumanoidComponent = Avatar.Humanoid;
        scale.PlayerSettings = State.PlayerSettings;
        Player = Avatar.SceneRoot.AddComponent<VRPlayerCharacterComponent>()!;
        Player.HumanoidComponent = Avatar.Humanoid;
        Player.IKSolver = Avatar.Solver;
        Player.HeightScaleComponent = scale;
        Player.TrackerCollection = trackers;
        Player.Headset = Head;
        Player.LeftController = Left;
        Player.RightController = Right;
        Player.PlayspaceRoot = (Transform)Avatar.Playspace.Transform;
        UpdateMatrices(Avatar.SceneRoot.Transform);
        InvokePlayer("OnComponentActivated");
    }

    public void Tick()
    {
        Head.RecalcLocal();
        Left.RecalcLocal();
        Right.RecalcLocal();
        foreach (var tracker in Player.TrackerCollection!.Trackers.Values)
            tracker.Item2.RecalcLocal();
        UpdateMatrices(Avatar.SceneRoot.Transform);
        InvokePlayer("UpdateTick");
        UpdateMatrices(Avatar.SceneRoot.Transform);
    }

    public void Advance(long nanoseconds = 50_000_000)
    {
        State.Snapshot = State.Snapshot with { SnapshotId = State.Snapshot.SnapshotId + 1, SampleTime = State.Snapshot.SampleTime + nanoseconds };
        for (int i = 0; i < State.TrackerPoses.Length; i++)
            State.TrackerPoses[i] = State.TrackerPoses[i] with
            {
                Info = State.TrackerPoses[i].Info with { SnapshotId = State.Snapshot.SnapshotId, SampleTime = State.Snapshot.SampleTime },
            };
        Tick();
    }

    public void AddThreeBodyTrackers()
    {
        TransformBase[] bones = [Avatar.Humanoid.Hips.Node!.Transform, Avatar.Humanoid.Left.Foot.Node!.Transform, Avatar.Humanoid.Right.Foot.Node!.Transform];
        State.TrackerPoses = new RuntimeVrTrackerPose[3];
        State.Trackers = new RuntimeVrTrackerInfo[3];
        for (int i = 0; i < bones.Length; i++)
        {
            string identity = $"/tracker/player-flow-{i}";
            Matrix4x4 pose = bones[i].WorldMatrix * Avatar.Playspace.Transform.InverseWorldMatrix;
            RuntimeVrTrackerInfo info = new(identity, identity, null, null, true, true)
            {
                SessionGeneration = State.Snapshot.SessionGeneration, SnapshotId = State.Snapshot.SnapshotId,
                SampleTime = State.Snapshot.SampleTime, Connected = true, Bound = true, ActionActive = true,
                PositionValid = true, OrientationValid = true, EverTracked = true, LastValidPose = pose,
            };
            State.TrackerPoses[i] = new(info, pose);
            State.Trackers[i] = info;
            VRTrackerTransform tracker = Player.TrackerCollection!.AddManualTracker(identity);
            tracker.SetParent(Avatar.Playspace.Transform, false, EParentAssignmentMode.Immediate);
            tracker.ApplyOpenXrTrackerInfo(info);
        }
        Tick();
    }

    public void Open()
    {
        Input.EmitBoolean("CalibrationOpen", false);
        Input.EmitBoolean("CalibrationOpen", true);
        Tick();
    }

    public void HoldStill()
    {
        for (int i = 0; i < 5; i++) Advance();
    }

    public void PullBothTriggers()
    {
        Input.EmitFloat("CalibrationCaptureLeft", 0);
        Input.EmitFloat("CalibrationCaptureRight", 0);
        Input.EmitFloat("CalibrationCaptureLeft", 1);
        Input.EmitFloat("CalibrationCaptureRight", 1);
        Tick();
    }

    public int OwnedTargetCount => Head.ChildCount + Left.ChildCount + Right.ChildCount
        + Player.TrackerCollection!.Trackers.Values.Sum(t => t.Item2.ChildCount);

    private void InvokePlayer(string method)
        => typeof(VRPlayerCharacterComponent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Player, null);

    private static void UpdateMatrices(TransformBase transform)
    {
        transform.RecalculateMatrices(true, true);
        foreach (TransformBase child in transform.Children) UpdateMatrices(child);
    }

    public void Dispose()
    {
        try
        {
            InvokePlayer("OnComponentDeactivated");
            Avatar.Dispose();
        }
        finally
        {
            RuntimeVrStateServices.Current = _previousState;
            RuntimeVrInputServices.Current = _previousInput;
        }
    }
}
