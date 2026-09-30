using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Components.Scene;
using XREngine.Input;

namespace XREngine.UnitTests.Animation;

/// <summary>Exercises real transform retention separately from current tracking validity.</summary>
[NonParallelizable]
public class VRTrackingValidityTests
{
    [Test]
    public void SimulationOwner_FreezesPredictedPoseWhileLatePoseAdvancesIndependently()
    {
        var previous = RuntimeVrStateServices.Current;
        var service = new StubVrTrackingServices { SnapshotAvailable = true };
        RuntimeVrStateServices.Current = service;
        var owner = new object();
        var headset = new XREngine.Scene.Transforms.VRHeadsetTransform();
        try
        {
            var first = new RuntimeVrTrackingSnapshot(1, 10, 100, Matrix4x4.CreateTranslation(1, 2, 3), true,
                Matrix4x4.Identity, true, Matrix4x4.Identity, true);
            service.Snapshot = first;
            headset.PublishSimulationPose(owner, first, first.HeadPose, true).ShouldBeTrue();
            service.Snapshot = first with { SnapshotId = 11, HeadPose = Matrix4x4.CreateTranslation(4, 5, 6) };
            headset.TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out Matrix4x4 predicted).ShouldBeTrue();
            predicted.ShouldBe(first.HeadPose);
            headset.TrackingSnapshotId.ShouldBe(10);
            headset.TryGetCurrentLocalPose(RuntimeVrPoseTiming.Recalc, out Matrix4x4 late).ShouldBeTrue();
            late.ShouldBe(service.Snapshot.HeadPose);
            headset.PublishSimulationPose(new object(), service.Snapshot, late, true).ShouldBeFalse();
            headset.PublishSimulationPose(owner, service.Snapshot, late, true).ShouldBeTrue();
            headset.TrackingSnapshotId.ShouldBe(11);
            headset.ReleaseSimulationPose(owner);
            headset.TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out predicted).ShouldBeTrue();
            predicted.ShouldBe(late);
        }
        finally { RuntimeVrStateServices.Current = previous; }
    }

    [Test]
    public void RealSource_LostTrackingRetainsPoseWithoutBecomingUsable()
    {
        var source = new ControlledVrPoseSource
        {
            CurrentValid = true,
            CurrentPose = Matrix4x4.CreateTranslation(2.0f, 1.2f, -3.0f),
        };
        Matrix4x4 tracked = source.Evaluate();
        source.PoseCurrentlyUsable.ShouldBeTrue();
        source.CurrentValid = false;
        source.LocalMatrixOffset = Matrix4x4.CreateTranslation(100.0f, 0.0f, 0.0f);
        source.Evaluate().ShouldBe(tracked);
        source.PoseCurrentlyUsable.ShouldBeFalse();
        source.TryGetCurrentLocalPose(RuntimeVrPoseTiming.Predicted, out _).ShouldBeFalse();
    }

    [Test]
    public void RealSource_ReconnectionUsesNewSample()
    {
        var source = new ControlledVrPoseSource { CurrentValid = true, CurrentPose = Matrix4x4.CreateTranslation(1, 2, 3) };
        source.Evaluate();
        source.CurrentValid = false;
        source.Evaluate();
        source.CurrentPose = Matrix4x4.CreateTranslation(-3, 2, 1);
        source.CurrentValid = true;
        source.Evaluate().ShouldBe(source.CurrentPose);
        source.PoseCurrentlyUsable.ShouldBeTrue();
    }

    [Test]
    public void TrackerHistory_DoesNotImplyCurrentValidity()
    {
        RuntimeVrTrackerInfo tracked = ValidTracker();
        tracked.PoseCurrentlyUsable.ShouldBeTrue();
        RuntimeVrTrackerInfo lost = tracked with { PoseAvailable = false, PositionValid = false };
        lost.EverTracked.ShouldBeTrue();
        lost.LastValidPose.ShouldBe(tracked.LastValidPose);
        lost.PoseCurrentlyUsable.ShouldBeFalse();
        lost.DiagnosticState.ShouldBe("Tracking lost");
        (tracked with { IsStale = true }).PoseCurrentlyUsable.ShouldBeFalse();
        (tracked with { Connected = false }).PoseCurrentlyUsable.ShouldBeFalse();
        (tracked with { ActionActive = false }).PoseCurrentlyUsable.ShouldBeFalse();
        (tracked with { OrientationValid = false }).PoseCurrentlyUsable.ShouldBeFalse();
    }

    [Test]
    public void TrackerRoleChange_PreservesPhysicalIdentityAndSample()
    {
        RuntimeVrTrackerInfo tracked = ValidTracker();
        RuntimeVrTrackerInfo changed = tracked with { RolePath = "/user/vive_tracker_htcx/role/camera", RoleName = "camera" };
        changed.UserPath.ShouldBe(tracked.UserPath);
        changed.PersistentPath.ShouldBe(tracked.PersistentPath);
        changed.SessionGeneration.ShouldBe(tracked.SessionGeneration);
        changed.LastValidPose.ShouldBe(tracked.LastValidPose);
    }

    [Test]
    public void Discovery_DoesNotClaimDisabledRuntimeStateOrInventTracking()
    {
        RuntimeVrTrackerInfo tracker = ValidTracker() with { Connected = false, PoseAvailable = false };
        tracker.DiagnosticState.ShouldContain("disabled state unknown");
        tracker.PoseCurrentlyUsable.ShouldBeFalse();
        tracker = tracker with { Connected = true, Bound = false, RequiresInputRebuild = true };
        tracker.DiagnosticState.ShouldContain("restart VR");
        tracker.PoseCurrentlyUsable.ShouldBeFalse();
    }

    private static RuntimeVrTrackerInfo ValidTracker()
        => new("/tracker/device-a", "/tracker/device-a", null, null, true, true)
        {
            SessionGeneration = 7,
            SnapshotId = 10,
            Connected = true,
            Bound = true,
            ActionActive = true,
            PositionValid = true,
            OrientationValid = true,
            EverTracked = true,
            LastValidSampleTime = 100,
            LastValidPose = Matrix4x4.CreateTranslation(1, 2, 3),
        };

}
