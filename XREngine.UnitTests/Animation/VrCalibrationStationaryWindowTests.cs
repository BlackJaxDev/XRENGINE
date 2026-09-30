using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.VR;
using XREngine.Input;

namespace XREngine.UnitTests.Animation;

public class VrCalibrationStationaryWindowTests
{
    [Test]
    public void StableContiguousSamples_RequireElapsedWindowAndIgnoreDuplicateIds()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses();
        window.Observe(Snapshot(1, 0), poses).ShouldBeFalse();
        window.Observe(Snapshot(2, 100_000_000), poses).ShouldBeFalse();
        for (int i = 0; i < 10; i++)
            window.Observe(Snapshot(2, 100_000_000), poses).ShouldBeFalse();
        window.StationarySeconds.ShouldBe(0.1f, 1e-6f);
        window.Observe(Snapshot(3, 200_000_000), poses).ShouldBeTrue();
    }

    [Test]
    public void CapturePublicationTrackerMotion_RejectsPreviouslyStationaryWindow()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses(11);
        window.Observe(Snapshot(1, 0), poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
        poses[10] = poses[10] with { WorldPose = Matrix4x4.CreateTranslation(1, 0, 0) };
        window.Observe(Snapshot(3, 210_000_000), poses).ShouldBeFalse();
        window.StationarySeconds.ShouldBe(0);
    }

    [Test]
    public void RotationWithoutTranslation_RejectsFastMotion()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses();
        window.Observe(Snapshot(1, 0), poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
        poses[0] = poses[0] with { WorldPose = Matrix4x4.CreateRotationY(0.5f) };
        window.Observe(Snapshot(3, 210_000_000), poses).ShouldBeFalse();
    }

    [Test]
    public void ReorderedSources_PreserveOneWindow()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses(11);
        window.Observe(Snapshot(1, 0), poses);
        Array.Reverse(poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
    }

    [Test]
    public void GapSessionAndReferenceChanges_ResetHistory()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses();
        window.Observe(Snapshot(1, 0), poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
        window.Observe(Snapshot(3, 600_000_000), poses).ShouldBeFalse();
        window.Observe(Snapshot(4, 800_000_000), poses).ShouldBeTrue();
        window.Observe(Snapshot(5, 900_000_000) with { ReferenceSpaceVersion = 1 }, poses).ShouldBeFalse();
        window.Observe(Snapshot(6, 1_100_000_000) with { ReferenceSpaceVersion = 1 }, poses).ShouldBeTrue();
        window.Observe(Snapshot(1, 1_200_000_000) with { SessionGeneration = 2 }, poses).ShouldBeFalse();
    }

    [Test]
    public void MissingDuplicateOrReplacedSources_ResetHistory()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses(4);
        window.Observe(Snapshot(1, 0), poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
        poses[3] = new(new ControlledVrPoseSource(), Matrix4x4.Identity);
        window.Observe(Snapshot(3, 210_000_000), poses).ShouldBeFalse();
        window.Observe(Snapshot(4, 410_000_000), poses).ShouldBeTrue();
        poses[3] = poses[0];
        window.Observe(Snapshot(5, 420_000_000), poses).ShouldBeFalse();
        window.Observe(Snapshot(6, 430_000_000), poses.AsSpan(0, 2)).ShouldBeFalse();
    }

    [Test]
    public void NonFiniteSampleAndLostHeadTracking_ResetHistory()
    {
        var window = new VrCalibrationStationaryWindow();
        VrCalibrationPose[] poses = Poses();
        window.Observe(Snapshot(1, 0), poses);
        window.Observe(Snapshot(2, 200_000_000), poses).ShouldBeTrue();
        window.Observe(Snapshot(3, 210_000_000) with { HeadValid = false }, poses).ShouldBeFalse();
        poses[1] = poses[1] with { WorldPose = Matrix4x4.CreateTranslation(float.NaN, 0, 0) };
        window.Observe(Snapshot(4, 410_000_000), poses).ShouldBeFalse();
    }

    private static RuntimeVrTrackingSnapshot Snapshot(long id, long time)
        => new(1, id, time, Matrix4x4.Identity, true, Matrix4x4.Identity, true, Matrix4x4.Identity, true);

    private static VrCalibrationPose[] Poses(int count = 3)
    {
        var result = new VrCalibrationPose[count];
        for (int i = 0; i < count; i++)
            result[i] = new(new ControlledVrPoseSource(), Matrix4x4.Identity);
        return result;
    }
}
