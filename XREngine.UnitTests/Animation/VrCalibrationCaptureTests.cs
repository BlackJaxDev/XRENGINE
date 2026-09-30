using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Core;

namespace XREngine.UnitTests.Animation;

[TestFixture, NonParallelizable]
public sealed class VrCalibrationCaptureTests
{
    [Test]
    public void RotatedMountAndNonOriginPlayspace_ReproduceDisplayedFootWithoutChangingScale()
    {
        using var rig = new SyntheticVrCalibrationRig();
        ((XREngine.Scene.Transforms.Transform)rig.Playspace.Transform).Translation = new(1.2f, 0.3f, -2);
        rig.LeftFoot.SetPose(new(-0.5f, 0.12f, 0.2f), Quaternion.CreateFromYawPitchRoll(1.1f, -0.4f, 0.7f));
        rig.RightFoot.SetPose(new(0.6f, 0.05f, 0.15f), Quaternion.CreateFromYawPitchRoll(-0.8f, 0.3f, -0.2f));
        rig.Solver.Root!.Scale = new(1.2f);
        rig.Solver.Solver.IKPositionWeight = 0;
        rig.Tick();
        Vector3 scale = rig.Solver.Root.Scale;
        Matrix4x4 expected = rig.Humanoid.Left.Toes.Node!.Transform.WorldMatrix;
        VrCalibrationMath.TryGetRigidPose(expected, out expected).ShouldBeTrue();
        var result = RuntimeVRIKCalibrator.Calibrate(rig.Solver, rig.Settings, rig.Head, rig.Hips,
            rig.LeftHand, rig.RightHand, rig.LeftFoot, rig.RightFoot);
        result.Success.ShouldBeTrue(result.Message);
        AssertMatrix(expected, rig.Solver.Solver.LeftLeg.Target!.WorldMatrix);
        rig.Solver.Root.Scale.ShouldBe(scale);
        Matrix4x4 offset = rig.Solver.Solver.LeftLeg.Target.LocalMatrix;
        rig.LeftFoot.SetPose(new(-0.35f, 0.2f, 0.3f), Quaternion.CreateFromYawPitchRoll(1.3f, -0.2f, 0.8f));
        rig.Tick();
        AssertMatrix(offset * rig.LeftFoot.WorldMatrix, rig.Solver.Solver.LeftLeg.Target.WorldMatrix);
    }

    [Test]
    public void InvalidCapture_RestoresExistingTargetIdentitiesMatricesAndWeights()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        var targets = rig.GetSolverTargets();
        Matrix4x4[] locals = targets.Select(t => t!.LocalMatrix).ToArray();
        float weight = rig.Solver.Solver.Spine.HipsPositionWeight;
        rig.Settings.HipPositionWeight = 0.4f;
        rig.RightFoot.Pose = new Matrix4x4();
        var result = RuntimeVRIKCalibrator.Calibrate(rig.Solver, rig.Settings, rig.Head, rig.Hips,
            rig.LeftHand, rig.RightHand, rig.LeftFoot, rig.RightFoot);
        result.Success.ShouldBeFalse();
        for (int i = 0; i < targets.Length; i++)
        {
            rig.GetSolverTargets()[i].ShouldBeSameAs(targets[i]);
            targets[i]!.LocalMatrix.ShouldBe(locals[i]);
        }
        rig.Solver.Solver.Spine.HipsPositionWeight.ShouldBe(weight);
        rig.CountTargetNodes().ShouldBe(6);
    }

    [TestCase(0, 0, true)]
    [TestCase(1.3f, 0, true)]
    [TestCase(0, 0.3f, false)]
    public void HeadTiltRefusal_IsIndependentOfYaw(float yaw, float tilt, bool accepted)
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Head.SetPose(new(0, 1.7f, 0), Quaternion.CreateFromYawPitchRoll(yaw, tilt, 0));
        var result = RuntimeVRIKCalibrator.Calibrate(rig.Solver, rig.Settings, rig.Head);
        result.Success.ShouldBe(accepted, result.Message);
        rig.CountTargetNodes().ShouldBe(accepted ? 1 : 0);
    }

    [Test]
    public void MissingSettingsAndUnavailablePose_AreTypedFailuresWithoutMutation()
    {
        using var rig = new SyntheticVrCalibrationRig();
        RuntimeVRIKCalibrator.Calibrate(rig.Solver, null, rig.Head).Success.ShouldBeFalse();
        rig.Head.Connected = false;
        RuntimeVRIKCalibrator.Calibrate(rig.Solver, rig.Settings, rig.Head).Success.ShouldBeFalse();
        rig.CountTargetNodes().ShouldBe(0);
    }

    [Test]
    public void CaptureOffset_IsAsIsForArbitraryRigidFramesAndRejectsInvalidMatrices()
    {
        Matrix4x4 target = Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(4, 1, -3);
        Matrix4x4 device = Matrix4x4.CreateFromYawPitchRoll(-0.5f, 1, 0.2f) * Matrix4x4.CreateTranslation(4.3f, 0.8f, -2.7f);
        VrCalibrationMath.TryCaptureOffset(target, device, out Matrix4x4 offset).ShouldBeTrue();
        AssertMatrix(target, offset * device);
        VrCalibrationMath.TryCaptureOffset(target, default, out _).ShouldBeFalse();
        device.M11 = float.NaN;
        VrCalibrationMath.TryCaptureOffset(target, device, out _).ShouldBeFalse();
    }

    [Test]
    public void RepeatedDirectCapture_PreservesFixedEyeAndControllerOffsetsExactlyOnce()
    {
        using var rig = new SyntheticVrCalibrationRig();
        Matrix4x4 eye = Matrix4x4.CreateTranslation(0, -0.07f, 0.02f);
        Matrix4x4 hand = Matrix4x4.CreateRotationY(0.2f) * Matrix4x4.CreateTranslation(0.01f, -0.03f, 0.06f);
        rig.Humanoid.SetIKTarget(EHumanoidIKTarget.Head, rig.Head, eye);
        rig.Humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, rig.LeftHand, hand);
        rig.Calibrate().ShouldNotBeNull();
        var first = rig.GetSolverTargets();
        Matrix4x4 headLocal = first[0]!.LocalMatrix, handLocal = first[2]!.LocalMatrix;
        for (int repeat = 0; repeat < 4; repeat++)
        {
            rig.Calibrate().ShouldNotBeNull();
            AssertMatrix(headLocal, first[0]!.LocalMatrix);
            AssertMatrix(handLocal, first[2]!.LocalMatrix);
            rig.Humanoid.GetIKTarget(EHumanoidIKTarget.Head).offset.ShouldBe(Matrix4x4.Identity);
            rig.Humanoid.GetIKTarget(EHumanoidIKTarget.LeftHand).offset.ShouldBe(Matrix4x4.Identity);
        }
    }

    [Test]
    public void MatrixCallbackFailureDuringCommit_RestoresEarlierWritesAndExactSolverReferences()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        var targets = rig.GetSolverTargets();
        Matrix4x4[] before = targets.Select(t => t!.LocalMatrix).ToArray();
        var external = new XREngine.Scene.SceneNode(rig.SceneRoot, "External wrist", new XREngine.Scene.Transforms.Transform());
        rig.Solver.Solver.RightArm.Target = (XREngine.Scene.Transforms.Transform)external.Transform;
        rig.Humanoid.SetIKTarget(EHumanoidIKTarget.Head, rig.Head, Matrix4x4.CreateTranslation(0, -0.15f, 0));
        int callbacks = 0;
        void ThrowDuringCommit(XREngine.Scene.Transforms.TransformBase transform, Matrix4x4 matrix)
        {
            callbacks++;
            throw new InvalidOperationException("Injected matrix observer failure");
        }
        targets[2]!.LocalMatrixChanged += ThrowDuringCommit;
        try
        {
            var result = RuntimeVRIKCalibrator.Calibrate(rig.Solver, rig.Settings, rig.Head, rig.Hips, rig.LeftHand, rig.RightHand, rig.LeftFoot, rig.RightFoot);
            result.Success.ShouldBeFalse();
            callbacks.ShouldBeGreaterThan(0);
            for (int n = 0; n < targets.Length; n++)
                AssertMatrix(before[n], targets[n]!.LocalMatrix);
            rig.Solver.Solver.RightArm.Target.ShouldBeSameAs(external.Transform);
            rig.CountTargetNodes().ShouldBe(6);
        }
        finally { targets[2]!.LocalMatrixChanged -= ThrowDuringCommit; }
    }

    [Test]
    public void ElevenSlotCapture_PublishesAllPoseChannelsAndRetainsOneOffsetOwner()
    {
        using var rig = new SyntheticVrCalibrationRig();
        EHumanoidIKTarget[] additional = [EHumanoidIKTarget.Chest, EHumanoidIKTarget.LeftElbow, EHumanoidIKTarget.RightElbow, EHumanoidIKTarget.LeftKnee, EHumanoidIKTarget.RightKnee];
        XREngine.Scene.Transforms.TransformBase[] bones = [rig.Humanoid.Chest.Node!.Transform, rig.Humanoid.Left.Arm.Node!.Transform,
            rig.Humanoid.Right.Arm.Node!.Transform, rig.Humanoid.Left.Knee.Node!.Transform, rig.Humanoid.Right.Knee.Node!.Transform];
        var devices = new SyntheticVrDeviceTransform[5];
        for (int n = 0; n < additional.Length; n++)
        {
            devices[n] = new SyntheticVrDeviceTransform("extra-" + n, "irrelevant", (uint)(6 + n));
            _ = new XREngine.Scene.SceneNode(rig.Playspace, "Extra tracker", devices[n]);
            devices[n].SetPose(bones[n].WorldTranslation + new Vector3(0.03f, 0.01f, 0.02f), Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, -0.4f));
            rig.Humanoid.SetIKTarget(additional[n], devices[n], Matrix4x4.Identity);
        }
        rig.Calibrate().ShouldNotBeNull();
        var targets = additional.Select(rig.Solver.GetCalibratedTarget).ToArray();
        rig.Solver.Solver.Spine.ChestTarget.ShouldBeSameAs(targets[0]);
        rig.Solver.Solver.LeftArm.UpperArmTarget.ShouldBeSameAs(targets[1]);
        rig.Solver.Solver.RightArm.UpperArmTarget.ShouldBeSameAs(targets[2]);
        rig.Solver.Solver.LeftLeg.KneeTarget.ShouldBeSameAs(targets[3]);
        rig.Solver.Solver.RightLeg.KneeTarget.ShouldBeSameAs(targets[4]);
        rig.Solver.Solver.Spine.ChestTargetWeight.ShouldBe(1);
        rig.Solver.Solver.LeftArm.Settings.UpperArmTargetWeight.ShouldBe(1);
        rig.Solver.Solver.LeftLeg.KneeTargetWeight.ShouldBe(1);
        for (int n = 0; n < additional.Length; n++)
        {
            targets[n]!.Parent.ShouldBeSameAs(devices[n]);
            rig.Humanoid.GetIKTarget(additional[n]).offset.ShouldBe(Matrix4x4.Identity);
        }
        for (int tick = 0; tick < 4; tick++)
            rig.Tick();
        for (int n = 0; n < additional.Length; n++)
            rig.Solver.GetCalibratedTarget(additional[n]).ShouldBeSameAs(targets[n]);
    }

    [Test]
    public void TrackerLoss_HoldsThenFadesAndSamePhysicalSourceRecoversWithoutOriginSnap()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        var target = rig.Solver.Solver.LeftLeg.Target!;
        Matrix4x4 pose = target.WorldMatrix;
        rig.LeftFoot.Connected = false;
        rig.Solver.UpdateTrackingWeights(0.05f);
        rig.Solver.GetSlotPoseState(EHumanoidIKTarget.LeftFoot).Weight.ShouldBe(1);
        rig.Solver.UpdateTrackingWeights(0.15f);
        rig.Solver.GetSlotPoseState(EHumanoidIKTarget.LeftFoot).Weight.ShouldBe(0.5f, 0.0001f);
        rig.Solver.UpdateTrackingWeights(0.2f);
        rig.Solver.Solver.LeftLeg.PositionWeight.ShouldBe(0);
        target.WorldMatrix.ShouldBe(pose);
        rig.LeftFoot.Connected = true;
        rig.Solver.UpdateTrackingWeights(0.1f);
        rig.Solver.GetSlotPoseState(EHumanoidIKTarget.LeftFoot).Weight.ShouldBe(0.5f, 0.0001f);
        rig.Solver.UpdateTrackingWeights(0.1f);
        rig.Solver.Solver.LeftLeg.PositionWeight.ShouldBe(1);
        rig.Solver.Solver.LeftLeg.Target.ShouldBeSameAs(target);
    }

    [Test]
    public void HeadLossThenSuccessfulRecalibration_RestoresConfiguredHeadWeight()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Solver.Solver.Spine.PositionWeight = 0.8f;
        rig.Solver.Solver.Spine.RotationWeight = 0.7f;
        rig.Calibrate().ShouldNotBeNull();
        rig.Head.Connected = false;
        rig.Solver.UpdateTrackingWeights(1);
        rig.Solver.Solver.Spine.PositionWeight.ShouldBe(0);
        rig.Head.Connected = true;
        rig.Calibrate().ShouldNotBeNull();
        rig.Tick();
        rig.Solver.Solver.Spine.PositionWeight.ShouldBe(0.8f);
        rig.Solver.Solver.Spine.RotationWeight.ShouldBe(0.7f);
        rig.Solver.ClearTargets();
        rig.Calibrate().ShouldNotBeNull();
        rig.Tick();
        rig.Solver.Solver.Spine.PositionWeight.ShouldBe(0.8f);
    }

    [Test]
    public void ChangedPhysicalSession_NeverDrivesOldOffsetDuringHoldPeriod()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        rig.LeftFoot.SessionGeneration = 2;
        rig.LeftFoot.SetPose(new(40, 30, 20), Quaternion.Identity);
        rig.Tick();
        rig.Solver.Solver.LeftLeg.Target.ShouldBeNull();
        rig.Solver.Solver.LeftLeg.PositionWeight.ShouldBe(0);
        rig.Solver.GetSlotPoseState(EHumanoidIKTarget.LeftFoot).RequiresRecalibration.ShouldBeTrue();
        rig.Solver.GetSlotPoseState(EHumanoidIKTarget.LeftFoot).Weight.ShouldBe(0);
    }

    [Test]
    public void RotatedHeadBind_EyeGeometryStillLandsAtTheHeadset()
    {
        using var rig = new SyntheticVrCalibrationRig();
        var head = (XREngine.Scene.Transforms.Transform)rig.Humanoid.Head.Node!.Transform;
        head.Rotation = Quaternion.CreateFromYawPitchRoll(0.7f, 0.2f, -0.3f);
        head.SaveBindState();
        Vector3 localEye = new(0.02f, 0.08f, -0.05f);
        var method = typeof(XREngine.Components.VR.VRPlayerCharacterComponent).GetMethod("GetFixedEyeToHeadOffset",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Matrix4x4 offset = (Matrix4x4)method.Invoke(null, [rig.Humanoid, localEye])!;
        Matrix4x4 hmd = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(4, 1.7f, 5);
        Vector3.Distance(Vector3.Transform(localEye, offset * hmd), hmd.Translation).ShouldBeLessThan(0.00001f);
    }

    [Test]
    public void RoomScaleMovement_UsesCalibratedHipOffsetOnceInNonOriginPlayspace()
    {
        using var rig = new SyntheticVrCalibrationRig();
        var playspace = (XREngine.Scene.Transforms.Transform)rig.Playspace.Transform;
        playspace.Translation = new(3, 0, -2);
        playspace.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        rig.Hips.SetPose(new(0.2f, 1, -0.1f), Quaternion.Identity);
        rig.Calibrate().ShouldNotBeNull();
        rig.Hips.SetPose(new(0.3f, 1, 0.05f), Quaternion.Identity);
        rig.Tick();
        var movement = rig.SceneRoot.AddComponent<RecordingVrMovementComponent>()!;
        var player = rig.SceneRoot.AddComponent<XREngine.Components.VR.VRPlayerCharacterComponent>()!;
        player.HumanoidComponent = rig.Humanoid;
        player.IKSolver = rig.Solver;
        player.CharacterMovementComponent = movement;
        player.PlayspaceRoot = playspace;
        Matrix4x4 relative = rig.Solver.Solver.Spine.HipsTarget!.WorldMatrix * playspace.InverseWorldMatrix;
        Vector3 expected = playspace.WorldTranslation + new Vector3(relative.Translation.X, 0, relative.Translation.Z);
        var method = player.GetType().GetMethod("MovePlayer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(player, [rig.Solver.Root, playspace]);
        Vector3.Distance(movement.LastInput, expected).ShouldBeLessThan(0.0001f);
    }

    private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual)
    {
        Vector3.Distance(expected.Translation, actual.Translation).ShouldBeLessThan(0.0001f);
        Matrix4x4.Decompose(expected, out _, out Quaternion er, out _);
        Matrix4x4.Decompose(actual, out _, out Quaternion ar, out _);
        MathF.Abs(Quaternion.Dot(er, ar)).ShouldBeGreaterThan(0.99999f);
    }
}
