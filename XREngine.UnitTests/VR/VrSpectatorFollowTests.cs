using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.VR;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Animation;

namespace XREngine.UnitTests.VR;

[TestFixture]
[NonParallelizable]
public sealed class VrSpectatorFollowTests
{
    [Test]
    public void IdentityBody_TrailingCameraIsPositiveZ()
    {
        var settings = new VrSpectatorFollowSettings { Height = 0, Distance = 3 };
        var pose = new VrSpectatorFollowState().Evaluate(Matrix4x4.Identity, null, settings, 0);
        pose.UnobstructedPosition.ShouldBe(new Vector3(0, 0, 3));
    }

    [Test]
    public void BodyYaw_RotatesTrailingOffsetWithoutPitchOrRoll()
    {
        var root = Matrix4x4.CreateTranslation(5, 0, 2);
        var hips = Matrix4x4.CreateRotationY(MathF.PI / 2);
        hips.Translation = new(7, 1, 9);
        var pose = new VrSpectatorFollowState().Evaluate(root, hips, new() { Height = 0, Distance = 3 }, 0);
        Vector3.Distance(pose.UnobstructedPosition, new(8, 1, 2)).ShouldBeLessThan(1e-5f);
        pose.BoomOrigin.M22.ShouldBe(1f, 1e-5f);
    }

    [Test]
    public void MissingHips_UsesExplicitPlayerRootHeading()
    {
        var root = Matrix4x4.CreateRotationY(.7f);
        var settings = new VrSpectatorFollowSettings();
        var pose = new VrSpectatorFollowState().Evaluate(root, null, settings, 0);
        Vector3 direction = Vector3.Normalize(pose.UnobstructedPosition - pose.BoomOrigin.Translation);
        Vector3.Distance(direction, Vector3.TransformNormal(Vector3.UnitZ, root)).ShouldBeLessThan(1e-5f);
    }

    [Test]
    public void FixedTargetSmoothing_IsFrameRateIndependent()
    {
        Vector3 at30 = Advance(30), at120 = Advance(120);
        Vector3.Distance(at30, at120).ShouldBeLessThan(2e-5f);
        static Vector3 Advance(int steps)
        {
            var state = new VrSpectatorFollowState();
            var settings = new VrSpectatorFollowSettings { Smoothing = 4 };
            state.Evaluate(Matrix4x4.Identity, null, settings, 0);
            VrSpectatorFollowPose result = default;
            for (int i = 0; i < steps; ++i)
                result = state.Evaluate(Matrix4x4.CreateTranslation(10, 0, 0), null, settings, 1f / steps);
            return result.UnobstructedPosition;
        }
    }

    [Test]
    public void Reset_SnapsToNewBasisInsteadOfInterpolatingAcrossTeleport()
    {
        var state = new VrSpectatorFollowState();
        var settings = new VrSpectatorFollowSettings();
        state.Evaluate(Matrix4x4.Identity, null, settings, 0);
        state.Reset();
        var pose = state.Evaluate(Matrix4x4.CreateTranslation(100, 0, 0), null, settings, .001f);
        pose.BoomOrigin.M41.ShouldBe(100f);
    }

    [Test]
    public void DegenerateHeadingAndAim_StayFinite()
    {
        var pose = new VrSpectatorFollowState().Evaluate(new Matrix4x4(), null, new(), .01f);
        float.IsFinite(pose.UnobstructedPosition.X).ShouldBeTrue();
        VrSpectatorFollowState.LookAt(Vector3.Zero, Vector3.Zero, Quaternion.Identity).ShouldBe(Quaternion.Identity);
        Quaternion vertical = VrSpectatorFollowState.LookAt(Vector3.Zero, Vector3.UnitY, Quaternion.Identity);
        float.IsFinite(vertical.X).ShouldBeTrue();
        vertical.LengthSquared().ShouldBe(1f, 1e-5f);
    }

    [Test]
    public void InvalidSettings_AreRejectedAndDistanceIsBounded()
    {
        var settings = new VrSpectatorFollowSettings();
        Should.Throw<ArgumentOutOfRangeException>(() => settings.Distance = float.NaN);
        Should.Throw<ArgumentOutOfRangeException>(() => settings.AimOffset = new(float.PositiveInfinity, 0, 0));
        settings.Distance = -1;
        settings.Distance.ShouldBe(.1f);
    }

    [Test]
    public void DiscontinuityVersion_AdvancesWithoutSubscribers()
    {
        long before = RuntimeVrDiscontinuityServices.Version;
        RuntimeVrDiscontinuityServices.Publish(EVrPoseDiscontinuity.Recenter);
        RuntimeVrDiscontinuityServices.Version.ShouldBeGreaterThan(before);
    }

    [Test]
    public void BoomResetAndLongFrame_DoNotOvershoot()
    {
        var boom = new BoomTransform { MaxLength = 3, ZoomOutSpeed = 10, AutomaticUpdate = false };
        boom.Evaluate(10);
        boom.CurrentLength.ShouldBe(3f, 1e-5f);
        boom.MaxLength = 1;
        boom.Evaluate(.01f, reset: true);
        boom.CurrentLength.ShouldBe(1f);
    }

    [Test]
    public void EyeVisibilityScope_ChangesOnlyEyeMasksAndRestoresThem()
    {
        var left = new TestEyeCamera { CullingMask = -1 };
        var right = new TestEyeCamera { CullingMask = -1 };
        using (var scope = new VrFirstPersonVisibilityScope([], left, right))
        {
            (left.CullingMask & (1 << 30)).ShouldBe(0);
            (right.CullingMask & (1 << 30)).ShouldBe(0);
            (left.CullingMask & 1).ShouldBe(1);
            scope.SetHiddenInEyes(false);
            (left.CullingMask & (1 << 30)).ShouldNotBe(0);
            scope.SetHiddenInEyes(true);
            (left.CullingMask & (1 << 30)).ShouldBe(0);
        }
        left.CullingMask.ShouldBe(-1);
        right.CullingMask.ShouldBe(-1);
    }

    [Test]
    public void OutputHasNoPublicationBeforeGpuCompletion()
    {
        var output = new VrSpectatorOutputComponent();
        output.TryAcquireCompletedOutput(out var lease).ShouldBeFalse();
        lease.ShouldBeNull();
        output.Width = 0;
        output.Width.ShouldBe(16u);
        Should.Throw<ArgumentOutOfRangeException>(() => output.FramesPerSecond = float.NaN);
    }

    [Test]
    public void CalibrationLabels_ReadTheSameAssignedSourcesAsCapture()
    {
        using var rig = new SyntheticVrCalibrationRig();
        VrCalibrationFeedbackComponent.DescribeBinding(rig.Humanoid, rig.Hips).ShouldBe("Hips");
        VrCalibrationFeedbackComponent.DescribeBinding(rig.Humanoid, rig.LeftFoot).ShouldBe("Left foot");
        VrCalibrationFeedbackComponent.DescribeBinding(rig.Humanoid, new Transform()).ShouldBe("Unassigned");
    }

    [Test]
    public void AppliedWorldPose_PreservesPositionUnderRotatedTranslatedScaledParent()
    {
        var parent = new Transform(new Vector3(2), new Vector3(12, 3, -7), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .8f));
        parent.RecalculateMatrices(forceWorldRecalc: true);
        var child = new Transform(parent);
        Matrix4x4 desired = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(-.3f, .2f, 0));
        desired.Translation = new(15, 2, -4);
        VrSpectatorFollowState.ApplyWorldPose(child, desired);
        Vector3.Distance(child.WorldMatrix.Translation, desired.Translation).ShouldBeLessThan(1e-4f);
        Vector3.Distance(Vector3.TransformNormal(-Vector3.UnitZ, child.WorldMatrix),
            Vector3.TransformNormal(-Vector3.UnitZ, desired)).ShouldBeLessThan(1e-4f);
    }
}
