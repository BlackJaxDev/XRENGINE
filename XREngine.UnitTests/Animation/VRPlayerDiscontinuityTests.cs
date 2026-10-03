using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.VR;
using XREngine.Input;

namespace XREngine.UnitTests.Animation;

/// <summary>Protects queued calibration invalidation when several coordinate cuts arrive before a simulation tick.</summary>
[NonParallelizable]
public sealed class VRPlayerDiscontinuityTests
{
    [Test]
    public void TrackingInvalidation_SurvivesLaterMovementOrCameraEvent(
        [Values(EVrPoseDiscontinuity.Recenter, EVrPoseDiscontinuity.SessionGeneration, EVrPoseDiscontinuity.AvatarReplacement)] EVrPoseDiscontinuity invalidation,
        [Values(EVrPoseDiscontinuity.Teleport, EVrPoseDiscontinuity.SnapTurn, EVrPoseDiscontinuity.CameraMode)] EVrPoseDiscontinuity following)
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.AddThreeBodyTrackers();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Hips).ShouldNotBeNull();

        RuntimeVrDiscontinuityServices.Publish(invalidation);
        RuntimeVrDiscontinuityServices.Publish(following);
        rig.Player.HasCommittedCalibration.ShouldBeTrue("events only queue simulation-owner work");
        rig.Advance();

        rig.Player.HasCommittedCalibration.ShouldBeFalse();
        rig.Player.CalibrationState.ShouldBe(EVrCalibrationState.Uncalibrated);
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Hips).ShouldBeNull();
        rig.Player.CalibrationMessage.ShouldContain("recalibrate");
    }

    [Test]
    public void MovementOrCameraEvent_PreservesCommittedCalibration(
        [Values(EVrPoseDiscontinuity.Teleport, EVrPoseDiscontinuity.SnapTurn, EVrPoseDiscontinuity.CameraMode)] EVrPoseDiscontinuity movement)
    {
        using var rig = new VrPlayerCalibrationTestRig();
        rig.Open();
        rig.AddThreeBodyTrackers();
        rig.HoldStill();
        rig.PullBothTriggers();
        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        var hips = rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Hips);

        RuntimeVrDiscontinuityServices.Publish(movement);
        rig.Advance();

        rig.Player.HasCommittedCalibration.ShouldBeTrue();
        rig.Avatar.Solver.GetCalibratedTarget(EHumanoidIKTarget.Hips).ShouldBeSameAs(hips);
    }
}
