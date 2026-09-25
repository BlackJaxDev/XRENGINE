using System;
using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>
/// A self-contained six-device humanoid rig for exercising runtime VRIK calibration.
/// Device order is head, hips, left hand, right hand, left foot, right foot.
/// </summary>
public sealed class SyntheticVrCalibrationRig : IDisposable
{
    private bool _disposed;

    public SceneNode SceneRoot { get; }
    public SceneNode Playspace { get; }
    public SceneNode AvatarRoot { get; }
    public HumanoidComponent Humanoid { get; }
    public VRIKSolverComponent Solver { get; }
    public VRIKCalibrationSettings Settings { get; } = new();

    public SyntheticVrDeviceTransform Head { get; }
    public SyntheticVrDeviceTransform Hips { get; }
    public SyntheticVrDeviceTransform LeftHand { get; }
    public SyntheticVrDeviceTransform RightHand { get; }
    public SyntheticVrDeviceTransform LeftFoot { get; }
    public SyntheticVrDeviceTransform RightFoot { get; }
    public SyntheticVrDeviceTransform[] Devices { get; }

    /// <summary>Number of completed VRIK post-update callbacks.</summary>
    public int PostUpdateCount { get; private set; }

    public SyntheticVrCalibrationRig()
    {
        SceneRoot = new SceneNode("Synthetic VR Scene", new Transform());
        Playspace = new SceneNode(SceneRoot, "Playspace", new Transform());
        AvatarRoot = new SceneNode(SceneRoot, "Avatar", new Transform());

        Head = AddDevice("Head", 0, new Vector3(0.0f, 1.7f, 0.0f));
        Hips = AddDevice("Hips", 1, new Vector3(0.0f, 1.0f, 0.0f));
        LeftHand = AddDevice("LeftHand", 2, new Vector3(-0.65f, 1.2f, 0.25f));
        RightHand = AddDevice("RightHand", 3, new Vector3(0.65f, 1.2f, 0.25f));
        LeftFoot = AddDevice("LeftFoot", 4, new Vector3(-0.15f, 0.08f, 0.12f));
        RightFoot = AddDevice("RightFoot", 5, new Vector3(0.15f, 0.08f, 0.12f));
        Devices = [Head, Hips, LeftHand, RightHand, LeftFoot, RightFoot];

        SceneNode hips = Bone(AvatarRoot, "Hips", 0.0f, 1.0f, 0.0f);
        SceneNode spine = Bone(hips, "Spine", 0.0f, 0.25f, 0.0f);
        SceneNode chest = Bone(spine, "Chest", 0.0f, 0.25f, 0.0f);
        SceneNode neck = Bone(chest, "Neck", 0.0f, 0.12f, 0.0f);
        SceneNode head = Bone(neck, "Head", 0.0f, 0.16f, 0.0f);

        SceneNode leftShoulder = Bone(chest, "LeftShoulder", -0.15f, 0.08f, 0.0f);
        SceneNode leftArm = Bone(leftShoulder, "LeftUpperArm", -0.24f, -0.08f, 0.0f);
        SceneNode leftElbow = Bone(leftArm, "LeftLowerArm", -0.25f, -0.12f, 0.0f);
        SceneNode leftWrist = Bone(leftElbow, "LeftHand", -0.20f, -0.08f, 0.0f);
        SceneNode rightShoulder = Bone(chest, "RightShoulder", 0.15f, 0.08f, 0.0f);
        SceneNode rightArm = Bone(rightShoulder, "RightUpperArm", 0.24f, -0.08f, 0.0f);
        SceneNode rightElbow = Bone(rightArm, "RightLowerArm", 0.25f, -0.12f, 0.0f);
        SceneNode rightWrist = Bone(rightElbow, "RightHand", 0.20f, -0.08f, 0.0f);

        SceneNode leftLeg = Bone(hips, "LeftUpperLeg", -0.15f, -0.11f, 0.0f);
        SceneNode leftKnee = Bone(leftLeg, "LeftLowerLeg", 0.0f, -0.42f, 0.04f);
        SceneNode leftFoot = Bone(leftKnee, "LeftFoot", 0.0f, -0.40f, 0.04f);
        SceneNode leftToes = Bone(leftFoot, "LeftToes", 0.0f, -0.04f, 0.15f);
        SceneNode rightLeg = Bone(hips, "RightUpperLeg", 0.15f, -0.11f, 0.0f);
        SceneNode rightKnee = Bone(rightLeg, "RightLowerLeg", 0.0f, -0.42f, 0.04f);
        SceneNode rightFoot = Bone(rightKnee, "RightFoot", 0.0f, -0.40f, 0.04f);
        SceneNode rightToes = Bone(rightFoot, "RightToes", 0.0f, -0.04f, 0.15f);

        UpdateMatrices(SceneRoot.Transform);
        Humanoid = AvatarRoot.AddComponent<HumanoidComponent>()!;
        Humanoid.Hips.Node = hips;
        Humanoid.Spine.Node = spine;
        Humanoid.Chest.Node = chest;
        Humanoid.Neck.Node = neck;
        Humanoid.Head.Node = head;
        Humanoid.Left.Shoulder.Node = leftShoulder;
        Humanoid.Left.Arm.Node = leftArm;
        Humanoid.Left.Elbow.Node = leftElbow;
        Humanoid.Left.Wrist.Node = leftWrist;
        Humanoid.Left.Leg.Node = leftLeg;
        Humanoid.Left.Knee.Node = leftKnee;
        Humanoid.Left.Foot.Node = leftFoot;
        Humanoid.Left.Toes.Node = leftToes;
        Humanoid.Right.Shoulder.Node = rightShoulder;
        Humanoid.Right.Arm.Node = rightArm;
        Humanoid.Right.Elbow.Node = rightElbow;
        Humanoid.Right.Wrist.Node = rightWrist;
        Humanoid.Right.Leg.Node = rightLeg;
        Humanoid.Right.Knee.Node = rightKnee;
        Humanoid.Right.Foot.Node = rightFoot;
        Humanoid.Right.Toes.Node = rightToes;
        Humanoid.PosePreviewMode = EHumanoidPosePreviewMode.AnimatedPose;

        // Match the raw device assignments made by VRPlayerCharacterComponent.
        Humanoid.SetIKTarget(EHumanoidIKTarget.Head, Head, Matrix4x4.Identity);
        Humanoid.SetIKTarget(EHumanoidIKTarget.Hips, Hips, Matrix4x4.Identity);
        Humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, LeftHand, Matrix4x4.Identity);
        Humanoid.SetIKTarget(EHumanoidIKTarget.RightHand, RightHand, Matrix4x4.Identity);
        Humanoid.SetIKTarget(EHumanoidIKTarget.LeftFoot, LeftFoot, Matrix4x4.Identity);
        Humanoid.SetIKTarget(EHumanoidIKTarget.RightFoot, RightFoot, Matrix4x4.Identity);

        Solver = AvatarRoot.AddComponent<VRIKSolverComponent>()!;
        Solver.AssignedHumanoid = Humanoid;
        Solver.IsActive = true;
        Solver.PoseBroadcastEnabled = false;
        Solver.Solver.LeftArm.WristToPalmAxis = Vector3.UnitZ;
        Solver.Solver.LeftArm.PalmToThumbAxis = -Vector3.UnitX;
        Solver.Solver.RightArm.WristToPalmAxis = Vector3.UnitZ;
        Solver.Solver.RightArm.PalmToThumbAxis = Vector3.UnitX;
        Solver.Solver.OnPostUpdate += CountPostUpdate;
        // Initialize references without solving an uncalibrated rig against empty targets.
        Solver.Solver.IKPositionWeight = 0.0f;
        Tick();
        Solver.Solver.IKPositionWeight = 1.0f;
    }

    /// <summary>Runs the same calibrator entry point used by VR player calibration.</summary>
    public VRIKCalibrator.CalibrationData? Calibrate()
    {
        UpdateMatrices(SceneRoot.Transform);
        var result = RuntimeVRIKCalibrator.Calibrate(Solver, Settings, Head, Hips, LeftHand, RightHand, LeftFoot, RightFoot)
            as VRIKCalibrator.CalibrationData;
        UpdateMatrices(SceneRoot.Transform);
        return result;
    }

    /// <summary>Returns the six solver-owned targets in device slot order.</summary>
    public TransformBase?[] GetSolverTargets()
        => [Solver.Solver.Spine.HeadTarget, Solver.Solver.Spine.HipsTarget,
            Solver.Solver.LeftArm.Target, Solver.Solver.RightArm.Target,
            Solver.Solver.LeftLeg.Target, Solver.Solver.RightLeg.Target];

    /// <summary>Counts calibrator-created target descendants under the six devices.</summary>
    public int CountTargetNodes()
    {
        int count = 0;
        foreach (SyntheticVrDeviceTransform device in Devices)
            count += CountTargetDescendants(device);
        return count;
    }

    /// <summary>Advances VRIK through its public external update path.</summary>
    public void Tick()
    {
        UpdateMatrices(SceneRoot.Transform);
        Solver.UpdateSolverExternal();
        UpdateMatrices(SceneRoot.Transform);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Solver.Solver.OnPostUpdate -= CountPostUpdate;
        SceneRoot.Destroy(true);
    }

    private SyntheticVrDeviceTransform AddDevice(string role, uint index, Vector3 position)
    {
        var device = new SyntheticVrDeviceTransform($"synthetic://device-{index:D2}", role, index);
        _ = new SceneNode(Playspace, role, device);
        device.SetPose(position, Quaternion.Identity);
        return device;
    }

    private static SceneNode Bone(SceneNode parent, string name, float x, float y, float z)
        => new(parent, name, new Transform(translation: new Vector3(x, y, z)));

    private static int CountTargetDescendants(TransformBase parent)
    {
        int count = 0;
        foreach (TransformBase child in parent.Children)
        {
            count++;
            count += CountTargetDescendants(child);
        }
        return count;
    }

    private void CountPostUpdate() => PostUpdateCount++;

    // The fixture has no world scheduler; publish matrices in parent-first order explicitly.
    private static void UpdateMatrices(TransformBase transform)
    {
        transform.RecalculateMatrices(true, true);
        foreach (TransformBase child in transform.Children)
            UpdateMatrices(child);
    }
}
