using System.Numerics;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class VRIKAdditionalTargetTests
{
    [Test]
    public void VirtualLimb_ReachesFixedReachableTarget()
    {
        var root = new SceneNode("Root", new Transform());
        var upper = new SceneNode(root, "Upper", new Transform());
        var lower = new SceneNode(upper, "Lower", new Transform(new Vector3(0, -1, 0.1f)));
        var end = new SceneNode(lower, "End", new Transform(new Vector3(0, -1, 0.1f)));
        root.Transform.RecalculateMatrixHierarchyImmediate();
        IKSolverVR.VirtualBone[] bones =
        [
            new(new((Transform)upper.Transform, false, false)),
            new(new((Transform)lower.Transform, false, false)),
            new(new((Transform)end.Transform, false, false)),
        ];
        IKSolverVR.VirtualBone.PreSolve(ref bones);
        Vector3 target = new(0.4f, -1.4f, 0.3f);
        IKSolverVR.VirtualBone.SolveTrigonometric(bones, 0, 1, 2, target, Vector3.UnitX, 1);
        float error = Vector3.Distance(target, bones[2].SolverPosition);
        TestContext.Out.WriteLine($"Virtual limb reachable-target error: {error:R}");
        Assert.That(error, Is.LessThan(0.0001f));
    }

    [Test]
    public void VirtualLimb_SkippedIntermediateBoneStillReachesTarget()
    {
        var root = new SceneNode("Root", new Transform());
        var upper = new SceneNode(root, "Upper", new Transform());
        var intermediate = new SceneNode(upper, "Intermediate", new Transform(new Vector3(0, -0.4f, 0)));
        var lower = new SceneNode(intermediate, "Lower", new Transform(new Vector3(0, -0.6f, 0.1f)));
        var end = new SceneNode(lower, "End", new Transform(new Vector3(0, -1, 0.1f)));
        root.Transform.RecalculateMatrixHierarchyImmediate();
        IKSolverVR.VirtualBone[] bones =
        [
            new(new((Transform)upper.Transform, false, false)),
            new(new((Transform)intermediate.Transform, false, false)),
            new(new((Transform)lower.Transform, false, false)),
            new(new((Transform)end.Transform, false, false)),
        ];
        IKSolverVR.VirtualBone.PreSolve(ref bones);
        Vector3 target = new(0.4f, -1.4f, 0.3f);
        IKSolverVR.VirtualBone.SolveTrigonometric(bones, 0, 2, 3, target, Vector3.UnitX, 1);
        float error = Vector3.Distance(target, bones[3].SolverPosition);
        TestContext.Out.WriteLine($"Virtual skipped-bone reachable-target error: {error:R}");
        Assert.That(error, Is.LessThan(0.0001f));
    }

    [TestCase(0, 1.70f)]
    [TestCase(1, 1.70f)]
    [TestCase(2, 1.70f)]
    [TestCase(0, 1.78f)]
    [TestCase(1, 1.78f)]
    [TestCase(2, 1.78f)]
    public void ChestTarget_RotatesChestWhileHeadAndHandsRemainConstrained(int axis, float headHeight)
    {
        using var rig = new SyntheticVrCalibrationRig();
        // Cover both a compressed torso and the original straight 1.78 m skeleton.
        rig.Head.SetPose(new Vector3(0, headHeight, 0), Quaternion.Identity);
        rig.LeftHand.SetPose(new Vector3(-0.5f, 1.5f, 0.2f), Quaternion.Identity);
        rig.RightHand.SetPose(new Vector3(0.5f, 1.5f, 0.2f), Quaternion.Identity);
        rig.Solver.Solver.LeftArm.Settings.ShoulderRotationWeight = 0;
        rig.Solver.Solver.RightArm.Settings.ShoulderRotationWeight = 0;
        Assert.That(rig.Calibrate(), Is.Not.Null);
        rig.Tick();
        rig.Tick();
        rig.Tick();
        Quaternion before = rig.Humanoid.Chest.Node!.Transform.WorldRotation;
        Vector3 hipsBefore = rig.Humanoid.Hips.Node!.Transform.WorldTranslation;
        Vector3 headBefore = rig.Humanoid.Head.Node!.Transform.WorldTranslation;
        Vector3 handBefore = rig.Humanoid.Left.Wrist.Node!.Transform.WorldTranslation;
        Quaternion rotation = Quaternion.CreateFromAxisAngle(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, 0.2f);
        Vector3 chestPosition = rig.Humanoid.Chest.Node.Transform.WorldTranslation;
        var target = new SceneNode(rig.SceneRoot, "ChestPose", new Transform(chestPosition, rotation * before));
        // Move the headset consistently with the chest swing so this tests a
        // reachable pose, rather than two incompatible hard constraints.
        Vector3 expectedHead = chestPosition + Vector3.Transform(headBefore - chestPosition, rotation);
        Matrix4x4 headPose = rig.Head.Pose;
        headPose.Translation += expectedHead - headBefore;
        rig.Head.Pose = headPose;
        rig.Humanoid.SetIKTarget(EHumanoidIKTarget.Chest, target.Transform, Matrix4x4.Identity);
        rig.Solver.Solver.Spine.ChestTargetWeight = 1;
        rig.Tick();
        Quaternion after = rig.Humanoid.Chest.Node.Transform.WorldRotation;
        float errorBefore = RotationError(before, target.Transform.WorldRotation);
        float errorAfter = RotationError(after, target.Transform.WorldRotation);
        float headMovement = Vector3.Distance(expectedHead, rig.Humanoid.Head.Node.Transform.WorldTranslation);
        float handMovement = Vector3.Distance(handBefore, rig.Humanoid.Left.Wrist.Node.Transform.WorldTranslation);
        TestContext.Out.WriteLine($"Chest rotation error {errorBefore:R} -> {errorAfter:R}; head error {headMovement:R}; hand movement {handMovement:R}");
        Assert.That(errorAfter, Is.LessThan(errorBefore * 0.5f));
        Assert.That(headMovement, Is.LessThan(0.03f));
        Assert.That(handMovement, Is.LessThan(0.03f));
        Assert.That(Vector3.Distance(hipsBefore, rig.Humanoid.Hips.Node!.Transform.WorldTranslation), Is.LessThan(0.001f));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void UpperArmTarget_ChangesShoulderAndElbowWithoutMovingWrist(bool left)
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.LeftHand.SetPose(new Vector3(-0.5f, 1.5f, 0.2f), Quaternion.Identity);
        rig.RightHand.SetPose(new Vector3(0.5f, 1.5f, 0.2f), Quaternion.Identity);
        rig.Solver.Solver.LeftArm.Settings.ShoulderRotationWeight = 0;
        rig.Solver.Solver.RightArm.Settings.ShoulderRotationWeight = 0;
        Assert.That(rig.Calibrate(), Is.Not.Null);
        rig.Tick();
        rig.Tick();
        rig.Tick();
        var side = left ? rig.Humanoid.Left : rig.Humanoid.Right;
        var armSolver = left ? rig.Solver.Solver.LeftArm : rig.Solver.Solver.RightArm;
        TransformBase arm = side.Arm.Node!.Transform;
        Vector3 shoulderBefore = arm.WorldTranslation;
        Vector3 elbowBefore = side.Elbow.Node!.Transform.WorldTranslation;
        Vector3 wristBefore = side.Wrist.Node!.Transform.WorldTranslation;
        var target = new SceneNode(rig.SceneRoot, "UpperArmPose", new Transform(
            arm.WorldTranslation + new Vector3(0, 0.12f, 0.02f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f) * arm.WorldRotation));
        rig.Humanoid.SetIKTarget(left ? EHumanoidIKTarget.LeftElbow : EHumanoidIKTarget.RightElbow, target.Transform, Matrix4x4.Identity);
        armSolver.Settings.UpperArmTargetWeight = 1;
        rig.Tick();
        float shoulderMovement = Vector3.Distance(shoulderBefore, arm.WorldTranslation);
        float elbowMovement = Vector3.Distance(elbowBefore, side.Elbow.Node.Transform.WorldTranslation);
        float wristMovement = Vector3.Distance(wristBefore, side.Wrist.Node.Transform.WorldTranslation);
        TestContext.Out.WriteLine($"Upper-arm movement {shoulderMovement:R}; elbow movement {elbowMovement:R}; wrist movement {wristMovement:R}");
        Assert.That(shoulderMovement, Is.GreaterThan(0.03f));
        Assert.That(elbowMovement, Is.GreaterThan(0.03f));
        Assert.That(wristMovement, Is.LessThan(0.03f));
    }

    [TestCase(0)]
    [TestCase(2)]
    public void IncompatibleChestSwing_PreservesHeadAndHipsPriority(int axis)
    {
        using var rig = new SyntheticVrCalibrationRig();
        Assert.That(rig.Calibrate(), Is.Not.Null);
        rig.Tick();
        rig.Tick();
        rig.Tick();
        Vector3 headBefore = rig.Humanoid.Head.Node!.Transform.WorldTranslation;
        Vector3 hipsBefore = rig.Humanoid.Hips.Node!.Transform.WorldTranslation;
        var target = new SceneNode(rig.SceneRoot, "ConflictingChestPose", new Transform(
            rig.Humanoid.Chest.Node!.Transform.WorldTranslation,
            Quaternion.CreateFromAxisAngle(axis == 0 ? Vector3.UnitX : Vector3.UnitZ, 1.2f)
                * rig.Humanoid.Chest.Node.Transform.WorldRotation));
        rig.Humanoid.SetIKTarget(EHumanoidIKTarget.Chest, target.Transform, Matrix4x4.Identity);
        rig.Solver.Solver.Spine.ChestTargetWeight = 1;
        rig.Tick();
        float headError = Vector3.Distance(headBefore, rig.Humanoid.Head.Node.Transform.WorldTranslation);
        float hipsError = Vector3.Distance(hipsBefore, rig.Humanoid.Hips.Node.Transform.WorldTranslation);
        float chestError = RotationError(rig.Humanoid.Chest.Node.Transform.WorldRotation, target.Transform.WorldRotation);
        TestContext.Out.WriteLine($"Conflicting chest residual {chestError:R}; head motion {headError:R}; hips motion {hipsError:R}");
        Assert.That(float.IsFinite(chestError), Is.True);
        Assert.That(headError, Is.LessThan(0.005f));
        Assert.That(hipsError, Is.LessThan(0.001f));
    }

    private static float RotationError(Quaternion first, Quaternion second)
        => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(first), Quaternion.Normalize(second))), 0, 1));
}
