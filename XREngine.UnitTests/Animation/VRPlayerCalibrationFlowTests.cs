using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.VR;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Exercises queued player commands and registered action callbacks against the production calibration flow.</summary>
[NonParallelizable]
public sealed class VRPlayerCalibrationFlowTests
{
    [Test]
    public void LostHips_DoesNotTurnHeadLeanIntoRoomScaleMovement()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.AddThreeBodyTrackers();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        var movement = rig.Avatar.SceneRoot.AddComponent<RecordingVrMovementComponent>()!;
        rig.Player.CharacterMovementComponent = movement;
        var hips = rig.State.TrackerPoses[0];
        rig.State.TrackerPoses[0] = hips with { Info = hips.Info with { PositionValid = false, OrientationValid = false } };
        Matrix4x4 head = rig.State.Snapshot.HeadPose;
        head.Translation += new Vector3(0.25f, 0, 0);
        rig.State.Snapshot = rig.State.Snapshot with { HeadPose = head };
        rig.Advance();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Hips).ShouldNotBeNull();
        movement.LastInput.X.ShouldBe(0f, 0.001f);
    }

    [Test]
    public void Activation_OffersHeadAndHandsWithoutStartingCalibrationOrBindingMute()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Player.IsCalibrating.ShouldBeFalse();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Uncalibrated);
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeFalse();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Head).ShouldNotBeNull();
        rig.Input.HasBoolean("CalibrationOpen").ShouldBeTrue();
        rig.Input.HasBoolean("CalibrationCancel").ShouldBeTrue();
        rig.Input.HasBoolean("ToggleMute").ShouldBeFalse();
        rig.Input.EmitBoolean("ToggleMute", true);
        rig.Input.EmitBoolean("ToggleMute", false);
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeFalse();
    }

    [Test]
    public void Open_IsQueuedAndPausesSolverOnlyOnSimulationTick()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Input.EmitBoolean("CalibrationOpen", true);
        rig.Player.IsCalibrating.ShouldBeFalse();
        rig.Avatar.Solver.IsActive.ShouldBeTrue();
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeTrue(rig.Player.CalibrationMessage);
        rig.Avatar.Solver.IsActive.ShouldBeFalse();
        rig.Input.EmitBoolean("CalibrationOpen", false);
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeTrue();
    }

    [Test]
    public void Cancel_RestoresRootPoseAllSourceTuplesSolverActivationAndWeights()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Tick();
        rig.Avatar.Humanoid.SetIKTarget(EHumanoidIKTarget.Hips, rig.Avatar.Hips, Matrix4x4.CreateTranslation(0.02f, 0.03f, -0.04f));
        rig.Avatar.AvatarRoot.Transform.DeriveLocalMatrix(Matrix4x4.CreateScale(1.1f) * Matrix4x4.CreateRotationY(0.2f) * Matrix4x4.CreateTranslation(2, 0.3f, -1));
        rig.Avatar.AvatarRoot.Transform.RecalculateMatrices(true, true);
        rig.Avatar.Solver.IsActive = false;
        rig.Avatar.Solver.Solver.Spine.HipsPositionWeight = 0.37f;
        rig.Avatar.Solver.Solver.LeftArm.Settings.PositionWeight = 0.42f;
        rig.Avatar.Solver.Solver.RightLeg.RotationWeight = 0.53f;
        Matrix4x4 root = rig.Avatar.AvatarRoot.Transform.LocalMatrix;
        var tuples = Enumerable.Range(0, 11).Select(i => rig.Avatar.Humanoid.GetIKTarget((EHumanoidIKTarget)i)).ToArray();
        float[] weights = CaptureWeights(rig);
        var locals = Descendants(rig.Avatar.AvatarRoot.Transform).ToDictionary(t => t, t => t.LocalMatrix);
        rig.Open();
        rig.Player.IsCalibrating.ShouldBeTrue(rig.Player.CalibrationMessage);
        rig.Input.EmitBoolean("CalibrationCancel", true);
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeFalse();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Uncalibrated);
        rig.Avatar.Solver.IsActive.ShouldBeFalse();
        CaptureWeights(rig).ShouldBe(weights);
        AssertMatrix(rig.Avatar.AvatarRoot.Transform.LocalMatrix, root);
        for (int i = 0; i < tuples.Length; i++)
            rig.Avatar.Humanoid.GetIKTarget((EHumanoidIKTarget)i).ShouldBe(tuples[i]);
        foreach (var saved in locals)
            AssertMatrix(saved.Key.LocalMatrix, saved.Value);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Capture_MissingHeadOrEitherControllerRemainsInCalibration(int missing)
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.Player.IsCalibrating.ShouldBeTrue(rig.Player.CalibrationMessage);
        rig.HoldStill();
        rig.State.Snapshot = missing switch
        {
            0 => rig.State.Snapshot with { HeadValid = false },
            1 => rig.State.Snapshot with { LeftControllerValid = false },
            _ => rig.State.Snapshot with { RightControllerValid = false },
        };
        rig.PullBothTriggers();
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Player.CalibrationMessage.ShouldContain("must be tracking");
        rig.Avatar.Solver.IsActive.ShouldBeFalse();
    }

    [Test]
    public void Capture_TiltedHeadIsRefusedWithSpecificFeedback()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.State.Snapshot = rig.State.Snapshot with
        {
            HeadPose = Matrix4x4.CreateRotationX(float.DegreesToRadians(25)) * Matrix4x4.CreateTranslation(0, 1.7f, 0),
        };
        rig.Open();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Player.CalibrationMessage.ShouldContain("head level");
    }

    [Test]
    public void TriggerPair_CapturesAfterStationaryWindowAndRepeatedCalibrationDoesNotGrowTargets()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.Player.IsCalibrating.ShouldBeTrue(rig.Player.CalibrationMessage);
        rig.HoldStill();
        rig.Input.EmitFloat("CalibrationCaptureLeft", 1);
        rig.Tick();
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Input.EmitFloat("CalibrationCaptureRight", 1);
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Tick();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Calibrated, rig.Player.CalibrationMessage);
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        rig.Avatar.Solver.IsActive.ShouldBeTrue();
        int count = rig.OwnedTargetCount;
        count.ShouldBe(3);
        for (int i = 0; i < 3; i++)
        {
            rig.Open();
            rig.HoldStill();
            rig.PullBothTriggers();
            rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Calibrated, rig.Player.CalibrationMessage);
            rig.OwnedTargetCount.ShouldBe(count);
        }
    }

    [Test]
    public void Capture_InsufficientStationaryTimeDoesNotCommit()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.PullBothTriggers();
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Player.CalibrationMessage.ShouldContain("Hold still");
    }

    [Test]
    public void SixPointFlow_CapturesAndCancelRestoresCommittedTargets()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.AddThreeBodyTrackers();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Calibrated, rig.Player.CalibrationMessage);
        rig.OwnedTargetCount.ShouldBe(6);
        var targets = Enumerable.Range(0, 6).Select(i => rig.Avatar.Solver.GetCalibratedTarget((EHumanoidIKTarget)i)).ToArray();
        targets.ShouldAllBe(t => t != null);
        var tuples = Enumerable.Range(0, 11).Select(i => rig.Avatar.Humanoid.GetIKTarget((EHumanoidIKTarget)i)).ToArray();
        float[] weights = CaptureWeights(rig);
        Matrix4x4 root = rig.Avatar.AvatarRoot.Transform.LocalMatrix;
        rig.Open();
        rig.HoldStill();
        rig.Player.CancelCalibration();
        rig.Tick();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Calibrated);
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        rig.Avatar.Solver.IsActive.ShouldBeTrue();
        rig.OwnedTargetCount.ShouldBe(6);
        AssertMatrix(rig.Avatar.AvatarRoot.Transform.LocalMatrix, root);
        CaptureWeights(rig).ShouldBe(weights);
        for (int i = 0; i < tuples.Length; i++)
            rig.Avatar.Humanoid.GetIKTarget((EHumanoidIKTarget)i).ShouldBe(tuples[i]);
        for (int i = 0; i < targets.Length; i++)
            rig.Avatar.Solver.GetCalibratedTarget((EHumanoidIKTarget)i).ShouldBeSameAs(targets[i]);
    }

    [Test]
    public void SixPointFlow_LossAtCaptureDoesNotSilentlyDropSelectedTracker()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.AddThreeBodyTrackers();
        rig.HoldStill();
        rig.State.TrackerPoses[0] = rig.State.TrackerPoses[0] with
        {
            Info = rig.State.TrackerPoses[0].Info with { PoseAvailable = false, PositionValid = false },
        };
        rig.PullBothTriggers();
        rig.Player.IsCalibrating.ShouldBeTrue();
        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Player.CalibrationMessage.ShouldContain("must be tracking");
    }

    [Test]
    public void SameSessionRecreatedRig_RestoresExactPhysicalBindingsWithoutRecapturingOffsets()
    {
        string playerIdentity = "session-player-" + Guid.NewGuid().ToString("N");
        Matrix4x4[] offsets;
        try
        {
            using (var first = new VrPlayerCalibrationTestRig())
            {
                first.Player.PlayerIdentity = playerIdentity;
                first.Player.AvatarIdentity = "synthetic-avatar-asset";
                first.Open();
                first.AddThreeBodyTrackers();
                first.HoldStill();
                first.PullBothTriggers();
                first.Player.HasCommittedCalibration.ShouldBeTrue();
                offsets = new[] { EHumanoidIKTarget.Hips, EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot }
                    .Select(slot => first.Avatar.Solver.GetCalibratedTarget(slot)!.LocalMatrix).ToArray();
            }
            using var second = new VrPlayerCalibrationTestRig();
            second.Player.PlayerIdentity = playerIdentity;
            second.Player.AvatarIdentity = "synthetic-avatar-asset";
            second.AddThreeBodyTrackers();
            second.Player.HasCommittedCalibration.ShouldBeTrue(second.Player.CalibrationMessage);
            second.Player.IsCalibrating.ShouldBeFalse();
            EHumanoidIKTarget[] slots = [EHumanoidIKTarget.Hips, EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot];
            for (int i = 0; i < slots.Length; i++)
                AssertMatrix(second.Avatar.Solver.GetCalibratedTarget(slots[i])!.LocalMatrix, offsets[i]);
            second.OwnedTargetCount.ShouldBe(6);
        }
        finally { VrCalibrationSessionStore.Shared.Remove(playerIdentity); }
    }

    [Test]
    public void HeadsetFirst_ControllersAppearingLaterGainFixedTargets()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.State.Snapshot = rig.State.Snapshot with { LeftControllerValid = false, RightControllerValid = false };
        rig.Tick();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Head).ShouldNotBeNull();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.LeftHand).ShouldBeNull();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.RightHand).ShouldBeNull();
        rig.State.Snapshot = rig.State.Snapshot with { LeftControllerValid = true, RightControllerValid = true };
        rig.Advance();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.LeftHand).ShouldNotBeNull();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.RightHand).ShouldNotBeNull();
        rig.Player.IsCalibrating.ShouldBeFalse();
    }

    [Test]
    public void AvatarReplacement_ReleasesPreviousOwnedTargetsAndRequiresNewRigInitialization()
    {
        using var rig = new VrPlayerCalibrationTestRig();
        using var replacement = new SyntheticVrCalibrationRig();
        rig.Open();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        rig.Player.HumanoidComponent = replacement.Humanoid;
        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Avatar.Solver.IsActive.ShouldBeFalse();
        rig.OwnedTargetCount.ShouldBe(0);
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Uncalibrated);
        rig.Player.IKSolver = replacement.Solver;
    }

    private static float[] CaptureWeights(VrPlayerCalibrationTestRig rig)
        => (float[])typeof(VRIKSolverComponent).GetMethod("CaptureCalibrationWeights", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig.Avatar.Solver, null)!;

    private static IEnumerable<TransformBase> Descendants(TransformBase root)
    {
        yield return root;
        foreach (TransformBase child in root.Children)
            foreach (TransformBase transform in Descendants(child))
                yield return transform;
    }

    private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
    {
        float[] a = [actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44];
        float[] e = [expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44];
        for (int i = 0; i < a.Length; i++) a[i].ShouldBe(e[i], 0.00001f);
    }
}
