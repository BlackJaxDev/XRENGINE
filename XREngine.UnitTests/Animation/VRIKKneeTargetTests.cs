using System.Numerics;
using NUnit.Framework;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class VRIKKneeTargetTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void KneeTarget_ChangesBendDirectionWithoutMovingFoot(bool left)
    {
        using var rig = new SyntheticVrCalibrationRig();
        Assert.That(rig.Calibrate(), Is.Not.Null);
        var device = left ? rig.LeftFoot : rig.RightFoot;
        Matrix4x4 lifted = device.Pose;
        lifted.Translation += new Vector3(0, 0.2f, 0);
        device.Pose = lifted;
        rig.Tick();
        var side = left ? rig.Humanoid.Left : rig.Humanoid.Right;
        var leg = left ? rig.Solver.Solver.LeftLeg : rig.Solver.Solver.RightLeg;
        Vector3 beforeKnee = side.Knee.Node!.Transform.WorldTranslation;
        Vector3 beforeFoot = side.Foot.Node!.Transform.WorldTranslation;
        var goal = new SceneNode(rig.SceneRoot, "KneePose", new Transform(beforeKnee + new Vector3(left ? -0.25f : 0.25f, 0, 0)));
        rig.Humanoid.SetIKTarget(left ? EHumanoidIKTarget.LeftKnee : EHumanoidIKTarget.RightKnee, goal.Transform, Matrix4x4.Identity);
        leg.KneeTargetWeight = 1;
        rig.Tick();
        Vector3 kneeDelta = side.Knee.Node.Transform.WorldTranslation - beforeKnee;
        float footMovement = Vector3.Distance(beforeFoot, side.Foot.Node.Transform.WorldTranslation);
        TestContext.Out.WriteLine($"Knee motion: {kneeDelta}; foot motion: {footMovement:R}");
        Assert.That(kneeDelta.X * (left ? -1 : 1), Is.GreaterThan(0.01f));
        Assert.That(footMovement, Is.LessThan(0.005f));
    }
}
