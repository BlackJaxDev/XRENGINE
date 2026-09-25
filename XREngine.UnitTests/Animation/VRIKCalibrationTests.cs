using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Exercises real calibration and solver updates with deterministic tracking inputs.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VRIKCalibrationTests
{
    private static readonly EHumanoidIKTarget[] Slots =
    [
        EHumanoidIKTarget.Head, EHumanoidIKTarget.Hips,
        EHumanoidIKTarget.LeftHand, EHumanoidIKTarget.RightHand,
        EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot,
    ];

    [Test]
    public void SyntheticDevice_SamplesKeepIdentityIndependentOfPoseAndAvailability()
    {
        using var rig = new SyntheticVrCalibrationRig();
        SyntheticVrDeviceTransform device = rig.LeftFoot;
        string identity = device.Identity;
        device.PoseCurrentlyUsable.ShouldBeTrue();
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
    public void Calibration_WithRawDeviceSlots_ReproducesTargetLossDuringSolverUpdates()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Solver.Solver.Initialized.ShouldBeTrue();
        rig.Devices.Length.ShouldBe(6);
        rig.Devices.Select(device => device.Identity).Distinct().Count().ShouldBe(6);
        rig.CountTargetNodes().ShouldBe(0);
        Matrix4x4 origin = rig.Playspace.Transform.WorldMatrix;
        Vector3 scale = rig.Solver.Root!.Scale;
        WriteSnapshot("before calibration", rig);

        rig.Calibrate().ShouldNotBeNull();
        TransformBase?[] calibrated = rig.GetSolverTargets();
        AssertCalibratedTargets(rig, calibrated);
        rig.CountTargetNodes().ShouldBe(6);
        WriteSnapshot("after calibration", rig);

        // Repeating calibration before synchronization reuses the six children.
        rig.Calibrate().ShouldNotBeNull();
        AssertSameTargets(calibrated, rig.GetSolverTargets());
        rig.CountTargetNodes().ShouldBe(6);

        int updates = rig.PostUpdateCount;
        for (int tick = 0; tick < 5; tick++)
        {
            rig.Tick();
            TransformBase?[] actual = rig.GetSolverTargets();
            for (int i = 0; i < Slots.Length; i++)
            {
                actual[i].ShouldBeNull($"Current raw-device synchronization clears {Slots[i]} on tick {tick}.");
                rig.Humanoid.GetIKTargetTransform(Slots[i]).ShouldBeSameAs(rig.Devices[i]);
                calibrated[i]!.Parent.ShouldBeSameAs(rig.Devices[i]);
            }
            rig.CountTargetNodes().ShouldBe(6, "Solver updates must not allocate additional target children.");
        }

        rig.PostUpdateCount.ShouldBe(updates + 5, "The test must execute the solver, not only synchronization.");
        rig.Solver.Root.Scale.ShouldBe(scale);
        rig.Playspace.Transform.WorldMatrix.ShouldBe(origin);
        rig.Solver.IsActive.ShouldBeTrue();
        WriteSnapshot("after five solver updates", rig);
    }

    [Test]
    public void Calibration_WithAuthoritativeConcreteTargets_PreservesBindingsAndSolvesMovedHand()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        TransformBase?[] calibrated = rig.GetSolverTargets();
        AssertCalibratedTargets(rig, calibrated);

        // Positive control for the harness: publish the targets into the store read by the solver.
        // Production calibration does not yet perform this publication.
        for (int i = 0; i < Slots.Length; i++)
            rig.Humanoid.SetIKTarget(Slots[i], calibrated[i], Matrix4x4.Identity);

        rig.Tick();
        Vector3 before = rig.Humanoid.Left.Wrist.Node!.Transform.WorldTranslation;
        Matrix4x4 moved = rig.LeftHand.Pose;
        moved.Translation += new Vector3(0.0f, 0.05f, -0.08f);
        rig.LeftHand.Pose = moved;
        int updates = rig.PostUpdateCount;
        for (int tick = 0; tick < 5; tick++)
        {
            rig.Tick();
            AssertSameTargets(calibrated, rig.GetSolverTargets());
            rig.CountTargetNodes().ShouldBe(6);
        }

        rig.PostUpdateCount.ShouldBe(updates + 5);
        Vector3 after = rig.Humanoid.Left.Wrist.Node.Transform.WorldTranslation;
        float distance = Vector3.Distance(before, after);
        float.IsFinite(distance).ShouldBeTrue();
        distance.ShouldBeGreaterThan(0.001f, "A changing controller pose must reach the avatar through actual IK solving.");
        WriteSnapshot("positive control after moving left hand", rig);
    }

    [Test]
    [Explicit("Known target-ownership defect: run explicitly to verify the required calibration contract before and after its repair.")]
    public void Calibration_TargetsRemainNonNullAndIdenticalAcrossSolverUpdates()
    {
        using var rig = new SyntheticVrCalibrationRig();
        rig.Calibrate().ShouldNotBeNull();
        TransformBase?[] calibrated = rig.GetSolverTargets();
        AssertCalibratedTargets(rig, calibrated);
        var observed = new TransformBase?[5][];
        int updates = rig.PostUpdateCount;
        for (int tick = 0; tick < observed.Length; tick++)
        {
            rig.Tick();
            observed[tick] = rig.GetSolverTargets();
        }

        rig.PostUpdateCount.ShouldBe(updates + observed.Length);
        using (Assert.EnterMultipleScope())
        {
            for (int tick = 0; tick < observed.Length; tick++)
                for (int i = 0; i < Slots.Length; i++)
                {
                    Assert.That(observed[tick][i], Is.Not.Null, $"{Slots[i]} at tick {tick}");
                    Assert.That(observed[tick][i], Is.SameAs(calibrated[i]), $"{Slots[i]} at tick {tick}");
                }

            Assert.That(rig.Calibrate(), Is.Not.Null);
            Assert.That(rig.CountTargetNodes(), Is.EqualTo(6), "Recalibration must not leak the previous six target nodes.");
        }
    }

    private static void AssertCalibratedTargets(SyntheticVrCalibrationRig rig, TransformBase?[] targets)
    {
        targets.Length.ShouldBe(6);
        targets.Distinct().Count().ShouldBe(6);
        for (int i = 0; i < Slots.Length; i++)
        {
            targets[i].ShouldNotBeNull($"Calibration must create {Slots[i]}.");
            targets[i].ShouldBeOfType<Transform>();
            targets[i]!.Parent.ShouldBeSameAs(rig.Devices[i]);
        }
    }

    private static void AssertSameTargets(TransformBase?[] expected, TransformBase?[] actual)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            actual[i].ShouldNotBeNull($"Missing {Slots[i]}.");
            actual[i].ShouldBeSameAs(expected[i], $"Replaced {Slots[i]}.");
        }
    }

    private static void WriteSnapshot(string label, SyntheticVrCalibrationRig rig)
        => TestContext.Out.WriteLine(
            $"{label}: targets={rig.GetSolverTargets().Count(target => target is not null)}, " +
            $"targetNodes={rig.CountTargetNodes()}, avatarScale={rig.Solver.Root!.Scale}, " +
            $"origin={rig.Playspace.Transform.WorldMatrix}, humanoidActive={rig.Humanoid.IsActive}, " +
            $"solverActive={rig.Solver.IsActive}, initialized={rig.Solver.Solver.Initialized}, updates={rig.PostUpdateCount}");
}
