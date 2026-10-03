using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Exercises calibrated target ownership and solver updates with deterministic tracked poses.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VRIKCalibrationTests
{
    private static readonly EHumanoidIKTarget[] OptionalSlots =
    [
        EHumanoidIKTarget.Hips, EHumanoidIKTarget.Chest, EHumanoidIKTarget.LeftFoot,
        EHumanoidIKTarget.RightFoot, EHumanoidIKTarget.LeftElbow, EHumanoidIKTarget.RightElbow,
        EHumanoidIKTarget.LeftKnee, EHumanoidIKTarget.RightKnee,
    ];

    [Test]
    public void SyntheticDevice_KeepsIdentityIndependentOfPoseAndAvailability()
    {
        using var rig = new SyntheticVrCalibrationRig();
        SyntheticVrDeviceTransform device = rig.LeftFoot;
        string identity = device.Identity;
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.6f, -0.3f, 0.2f);
        Vector3 position = new(-0.2f, 0.1f, 0.3f);
        device.SetPose(position, rotation, 1234);
        rig.Tick();
        device.Timestamp.ShouldBe(1234);
        device.WorldMatrix.ShouldBe(Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));

        device.PositionValid = false;
        device.PoseCurrentlyUsable.ShouldBeFalse();
        device.PositionValid = true;
        device.OrientationValid = false;
        device.PoseCurrentlyUsable.ShouldBeFalse();
        device.OrientationValid = true;
        device.Connected = false;
        device.PoseCurrentlyUsable.ShouldBeFalse();
        device.Connected = true;
        device.PoseCurrentlyUsable.ShouldBeTrue();
        device.Identity.ShouldBe(identity);
        device.Identity.ShouldNotBe(device.Role);
    }

    [Test]
    public void Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Solver.Solver.Initialized.ShouldBeTrue();
        rig.Devices.Select(device => device.Identity).Distinct().Count().ShouldBe(11);
        Matrix4x4 playspaceOrigin = rig.Playspace.Transform.WorldMatrix;
        Vector3 avatarScale = rig.Solver.Root!.Scale;

        rig.Calibrate().ShouldNotBeNull();
        TransformBase?[] targets = AssertOwnedTargets(rig);
        AssertSolverTargetsPublished(rig, targets);
        int updates = rig.PostUpdateCount;
        for (int tick = 0; tick < 5; tick++)
        {
            rig.Tick();
            AssertTargetIdentities(targets, rig.GetAllTargets());
            AssertSolverTargetsPublished(rig, targets);
        }
        rig.PostUpdateCount.ShouldBe(updates + 5);
        rig.CountDeviceTargetDescendants().ShouldBe(0);
        rig.Solver.Root.Scale.ShouldBe(avatarScale);
        rig.Playspace.Transform.WorldMatrix.ShouldBe(playspaceOrigin);

        rig.Calibrate().ShouldNotBeNull();
        AssertTargetIdentities(targets, rig.GetAllTargets());
        AssertSolverTargetsPublished(rig, targets);
    }

    [Test]
    public void TypedCapture_ThreeSixAndElevenPointsReuseAllTargets()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture().Success.ShouldBeTrue();
        TransformBase?[] targets = AssertOwnedTargets(rig);
        AssertWeight(rig, EHumanoidIKTarget.Head, 1f);
        AssertWeight(rig, EHumanoidIKTarget.LeftHand, 1f);
        AssertWeight(rig, EHumanoidIKTarget.RightHand, 1f);
        foreach (EHumanoidIKTarget slot in OptionalSlots)
            AssertWeight(rig, slot, 0f);

        rig.Capture(EHumanoidIKTarget.Hips, EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot)
            .Success.ShouldBeTrue();
        AssertTargetIdentities(targets, rig.GetAllTargets());
        AssertWeight(rig, EHumanoidIKTarget.Hips, 1f);
        AssertWeight(rig, EHumanoidIKTarget.LeftFoot, 1f);
        AssertWeight(rig, EHumanoidIKTarget.RightFoot, 1f);

        VrCalibrationResult full = rig.Capture(OptionalSlots);
        full.Success.ShouldBeTrue(full.Error);
        full.Slots.ShouldNotBeNull();
        full.Slots!.Count.ShouldBe(11);
        AssertTargetIdentities(targets, rig.GetAllTargets());
        for (int i = 0; i < rig.Devices.Length; i++)
        {
            EHumanoidIKTarget slot = (EHumanoidIKTarget)i;
            AssertWeight(rig, slot, 1f);
            VrCalibrationSlotState state = full.Slots[i];
            state.Target.ShouldBeSameAs(targets[i]);
            state.Device.ShouldBeSameAs(rig.Devices[i]);
            state.Identity.ShouldBe(rig.Devices[i].Identity);
        }
        rig.CountDeviceTargetDescendants().ShouldBe(0);
    }

    [Test]
    public void RejectedCapture_PreservesPublishedTargetsOffsetsWeightsAndSolverSettings()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture(OptionalSlots).Success.ShouldBeTrue();
        TransformBase?[] targets = AssertOwnedTargets(rig);
        VrCalibrationSlotState[] previous = new VrCalibrationSlotState[11];
        Matrix4x4[] worlds = new Matrix4x4[11];
        for (int i = 0; i < previous.Length; i++)
        {
            previous[i] = ReadSlot(rig, (EHumanoidIKTarget)i);
            worlds[i] = previous[i].Target.WorldMatrix;
        }
        float headHeight = rig.Solver.Solver.Spine.MinHeadHeight;
        bool plantFeet = rig.Solver.Solver.PlantFeet;
        TransformBase?[] solverTargets = rig.GetSolverTargets();

        VrCalibrationRequest request = rig.CreateRequest(OptionalSlots);
        Matrix4x4 invalid = rig.Chest.WorldMatrix;
        invalid.M41 = float.NaN;
        request.Slots[(int)EHumanoidIKTarget.Chest] = new VrCalibrationCapture(rig.Chest, invalid, rig.Chest.Identity);
        VrCalibrationResult result = rig.Solver.Calibrate(request);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();

        AssertTargetIdentities(targets, rig.GetAllTargets());
        for (int i = 0; i < previous.Length; i++)
        {
            VrCalibrationSlotState actual = ReadSlot(rig, (EHumanoidIKTarget)i);
            actual.Device.ShouldBeSameAs(previous[i].Device);
            actual.DeviceToTargetOffset.ShouldBe(previous[i].DeviceToTargetOffset);
            actual.Identity.ShouldBe(previous[i].Identity);
            actual.Weight.ShouldBe(previous[i].Weight);
            actual.Target.WorldMatrix.ShouldBe(worlds[i]);
        }
        AssertTargetIdentities(solverTargets, rig.GetSolverTargets());
        rig.Solver.Solver.Spine.MinHeadHeight.ShouldBe(headHeight);
        rig.Solver.Solver.PlantFeet.ShouldBe(plantFeet);
    }

    [Test]
    public void MixedTrackingSnapshot_RejectsCaptureWithoutChangingPublishedRig()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture(OptionalSlots).Success.ShouldBeTrue();
        TransformBase?[] targets = rig.GetAllTargets();
        VrCalibrationSlotState chest = ReadSlot(rig, EHumanoidIKTarget.Chest);
        Matrix4x4 targetWorld = chest.Target.WorldMatrix;
        VrCalibrationRequest request = rig.CreateRequest(OptionalSlots);
        request.Slots[(int)EHumanoidIKTarget.Chest] = new VrCalibrationCapture(
            rig.Chest, rig.Chest.WorldMatrix, rig.Chest.Identity, SnapshotId: 2, SampleTime: 20);

        VrCalibrationResult rejected = rig.Solver.Calibrate(request);
        rejected.Success.ShouldBeFalse();
        rejected.Error!.ShouldContain("Tracking samples changed");
        AssertTargetIdentities(targets, rig.GetAllTargets());
        ReadSlot(rig, EHumanoidIKTarget.Chest).DeviceToTargetOffset.ShouldBe(chest.DeviceToTargetOffset);
        chest.Target.WorldMatrix.ShouldBe(targetWorld);
    }

    [Test]
    public void LevelHeadCapture_RejectsTiltWithoutReplacingRigAndAcceptsYaw()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture(OptionalSlots).Success.ShouldBeTrue();
        TransformBase?[] targets = rig.GetAllTargets();
        VrCalibrationSlotState head = ReadSlot(rig, EHumanoidIKTarget.Head);
        Vector3 headPosition = rig.Head.Pose.Translation;

        rig.Head.SetPose(headPosition, Quaternion.CreateFromYawPitchRoll(0.45f, 0.35f, 0f));
        VrCalibrationResult rejected = rig.Solver.Calibrate(rig.CreateRequest(true, OptionalSlots));
        rejected.Success.ShouldBeFalse();
        rejected.Error!.ShouldContain("head level");
        AssertTargetIdentities(targets, rig.GetAllTargets());
        ReadSlot(rig, EHumanoidIKTarget.Head).DeviceToTargetOffset.ShouldBe(head.DeviceToTargetOffset);

        rig.Head.SetPose(headPosition, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.45f));
        VrCalibrationResult accepted = rig.Solver.Calibrate(rig.CreateRequest(true, OptionalSlots));
        accepted.Success.ShouldBeTrue(accepted.Error);
        AssertTargetIdentities(targets, rig.GetAllTargets());
    }

    [Test]
    public void CaptureWithRotatedPlayspaceScaledAvatarAndRotatedMounts_ComposesEveryTargetOnce()
    {
        using var rig = new SyntheticVrCalibrationRig();
        Transform playspace = (Transform)rig.Playspace.Transform;
        Transform avatar = (Transform)rig.AvatarRoot.Transform;
        playspace.Translation = new Vector3(2.4f, 0.3f, -1.7f);
        playspace.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.43f);
        avatar.Scale = new Vector3(1.28f);
        rig.LeftFoot.SetPose(new Vector3(-0.36f, 0.08f, 0.17f),
            Quaternion.CreateFromYawPitchRoll(0.7f, 0.2f, -0.1f));
        rig.RightFoot.SetPose(new Vector3(0.36f, 0.08f, 0.17f),
            Quaternion.CreateFromYawPitchRoll(-0.5f, 0.1f, 0.25f));
        rig.Chest.SetPose(new Vector3(0f, 1.5f, 0f),
            Quaternion.CreateFromYawPitchRoll(0.25f, -0.18f, 0.32f));
        rig.RefreshTransforms();
        Matrix4x4 avatarBefore = rig.AvatarRoot.Transform.WorldMatrix;
        VrCalibrationResult result = rig.Capture(OptionalSlots);
        result.Success.ShouldBeTrue(result.Error);
        TransformBase?[] targets = AssertOwnedTargets(rig);

        for (int i = 0; i < targets.Length; i++)
        {
            VrCalibrationSlotState state = ReadSlot(rig, (EHumanoidIKTarget)i);
            Matrix4x4 composed = state.DeviceToTargetOffset * rig.Devices[i].WorldMatrix;
            Vector3.Distance(state.Target.WorldTranslation, composed.Translation).ShouldBeLessThan(0.0002f,
                $"slot={i}, target={state.Target.WorldTranslation}, composed={composed.Translation}");
            Matrix4x4.Decompose(composed, out _, out Quaternion expectedRotation, out _).ShouldBeTrue();
            MathF.Abs(Quaternion.Dot(state.Target.WorldRotation, expectedRotation)).ShouldBeGreaterThan(0.9999f);
            rig.Solver.UpdateSlot((EHumanoidIKTarget)i, rig.Devices[i].WorldMatrix, 1f).ShouldBeTrue();
            Vector3.Distance(state.Target.WorldTranslation, composed.Translation).ShouldBeLessThan(0.0002f);
        }
        AssertTargetIdentities(targets, rig.GetAllTargets());
        rig.AvatarRoot.Transform.WorldMatrix.ShouldBe(avatarBefore);
        avatar.Scale.ShouldBe(new Vector3(1.28f));
    }

    [Test]
    public void EndingCalibrationPose_RestoresPreviousPreviewMode()
    {
        using var rig = new SyntheticVrCalibrationRig();
        EHumanoidPosePreviewMode previous = rig.Humanoid.PosePreviewMode;
        rig.Solver.ApplyCanonicalCalibrationPose(rig.Head.WorldMatrix).Success.ShouldBeTrue();
        rig.Humanoid.PosePreviewMode.ShouldBe(EHumanoidPosePreviewMode.TPose);
        rig.Solver.EndCalibrationPose();
        rig.Humanoid.PosePreviewMode.ShouldBe(previous);
    }

    [Test]
    public void MovedDevice_UsesFrozenOffsetAndKeepsAvatarOwnedTarget()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture(OptionalSlots).Success.ShouldBeTrue();
        TransformBase?[] targets = rig.GetAllTargets();
        VrCalibrationSlotState before = ReadSlot(rig, EHumanoidIKTarget.LeftFoot);
        Vector3 toeBefore = rig.Humanoid.Left.Toes.Node!.Transform.WorldTranslation;
        Matrix4x4 movedPose = Matrix4x4.CreateFromQuaternion(
            Quaternion.CreateFromYawPitchRoll(0.7f, -0.25f, 0.18f));
        movedPose.Translation = rig.LeftFoot.Pose.Translation + new Vector3(0.08f, 0.11f, -0.07f);
        rig.LeftFoot.Pose = movedPose;
        rig.Tick();

        rig.Solver.UpdateSlot(EHumanoidIKTarget.LeftFoot, rig.LeftFoot.WorldMatrix, 1f).ShouldBeTrue();
        VrCalibrationSlotState after = ReadSlot(rig, EHumanoidIKTarget.LeftFoot);
        after.Target.ShouldBeSameAs(before.Target);
        after.DeviceToTargetOffset.ShouldBe(before.DeviceToTargetOffset);
        Matrix4x4 expected = before.DeviceToTargetOffset * rig.LeftFoot.WorldMatrix;
        Vector3.Distance(after.Target.WorldTranslation, expected.Translation).ShouldBeLessThan(0.0001f);
        for (int tick = 0; tick < 3; tick++)
            rig.Tick();
        Vector3 toeAfter = rig.Humanoid.Left.Toes.Node.Transform.WorldTranslation;
        Vector3.Distance(toeBefore, toeAfter).ShouldBeGreaterThan(0.005f);
        AssertTargetIdentities(targets, rig.GetAllTargets());
        rig.CountDeviceTargetDescendants().ShouldBe(0);
    }

    [Test]
    public void MissingBoundTracker_RestoresOffsetAndRebindsWithoutReplacingTarget()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Capture(OptionalSlots).Success.ShouldBeTrue();
        EHumanoidIKTarget slot = EHumanoidIKTarget.LeftKnee;
        VrCalibrationSlotState saved = ReadSlot(rig, slot);
        TransformBase?[] targets = rig.GetAllTargets();

        VrCalibrationRequest restore = rig.CreateRequest(OptionalSlots);
        restore.Offsets[(int)slot] = saved.DeviceToTargetOffset;
        restore.Slots[(int)slot] = new VrCalibrationCapture(null, rig.LeftKnee.WorldMatrix,
            rig.LeftKnee.Identity);
        rig.Solver.Calibrate(restore).Success.ShouldBeTrue();
        VrCalibrationSlotState dormant = ReadSlot(rig, slot);
        dormant.Target.ShouldBeSameAs(saved.Target);
        dormant.Device.ShouldBeNull();
        dormant.Identity.ShouldBe(saved.Identity);
        dormant.DeviceToTargetOffset.ShouldBe(saved.DeviceToTargetOffset);
        dormant.Weight.ShouldBe(0f);
        AssertTargetIdentities(targets, rig.GetAllTargets());

        rig.Solver.RebindSlotDevice(slot, rig.LeftKnee, rig.LeftKnee.Identity).ShouldBeTrue();
        rig.Solver.UpdateSlot(slot, rig.LeftKnee.WorldMatrix, 1f).ShouldBeTrue();
        VrCalibrationSlotState restored = ReadSlot(rig, slot);
        restored.Target.ShouldBeSameAs(saved.Target);
        restored.Device.ShouldBeSameAs(rig.LeftKnee);
        restored.DeviceToTargetOffset.ShouldBe(saved.DeviceToTargetOffset);
        restored.Weight.ShouldBe(1f);
        AssertTargetIdentities(targets, rig.GetAllTargets());
    }

    private static TransformBase?[] AssertOwnedTargets(SyntheticVrCalibrationRig rig)
    {
        TransformBase?[] targets = rig.GetAllTargets();
        targets.Length.ShouldBe(11);
        targets.Distinct().Count().ShouldBe(11);
        for (int i = 0; i < targets.Length; i++)
        {
            TransformBase? target = targets[i];
            target.ShouldNotBeNull();
            target.ShouldBeOfType<Transform>();
            rig.Humanoid.GetIKTargetTransform((EHumanoidIKTarget)i).ShouldBeSameAs(target);
            TransformBase? ancestor = target!.Parent;
            while (ancestor is not null && !ReferenceEquals(ancestor, rig.AvatarRoot.Transform))
                ancestor = ancestor.Parent;
            ancestor.ShouldBeSameAs(rig.AvatarRoot.Transform);
        }
        rig.CountDeviceTargetDescendants().ShouldBe(0);
        return targets;
    }

    private static void AssertSolverTargetsPublished(SyntheticVrCalibrationRig rig, TransformBase?[] targets)
    {
        TransformBase?[] actual = rig.GetSolverTargets();
        for (int i = 0; i < actual.Length; i++)
            actual[i].ShouldBeSameAs(targets[i]);
    }

    private static void AssertTargetIdentities(TransformBase?[] expected, TransformBase?[] actual)
    {
        actual.Length.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++)
            actual[i].ShouldBeSameAs(expected[i]);
    }

    private static VrCalibrationSlotState ReadSlot(SyntheticVrCalibrationRig rig, EHumanoidIKTarget slot)
    {
        rig.Solver.TryGetSlot(slot, out VrCalibrationSlotState state).ShouldBeTrue();
        return state;
    }

    private static void AssertWeight(SyntheticVrCalibrationRig rig, EHumanoidIKTarget slot, float expected)
        => ReadSlot(rig, slot).Weight.ShouldBe(expected);
}
