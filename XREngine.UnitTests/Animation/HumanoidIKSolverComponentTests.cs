using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidIKSolverComponentTests
{
    [Test]
    public void LegacyAnimatedIKBridge_DoesNotResetCustomSolverSettings_AfterReactivation()
    {
        var root = new SceneNode("Root", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;

        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;

        solver._leftHand.IKPositionWeight = 0.25f;
        solver._leftHand.IKRotationWeight = 0.5f;
        solver._leftHand._bendModifier = ELimbBendModifier.Parent;
        solver._leftHand._bendModifierWeight = 0.35f;
        solver._leftHand._maintainRotationWeight = 0.55f;

        solver._spine.IKPositionWeight = 0.75f;
        solver._spine._tolerance = 0.125f;
        solver._spine._maxIterations = 11;
        solver._spine._useRotationLimits = true;

        solver.IsActive = false;
        solver.IsActive = true;

        humanoid.SetAnimatedHandPosition(new Vector3(1.0f, 2.0f, 3.0f), leftHand: true);
        humanoid.SetAnimatedHandRotation(Quaternion.Identity, leftHand: true);

        solver._leftHand.IKPositionWeight.ShouldBe(0.25f, 0.0001f);
        solver._leftHand.IKRotationWeight.ShouldBe(0.5f, 0.0001f);
        solver._leftHand._bendModifier.ShouldBe(ELimbBendModifier.Parent);
        solver._leftHand._bendModifierWeight.ShouldBe(0.35f, 0.0001f);
        solver._leftHand._maintainRotationWeight.ShouldBe(0.55f, 0.0001f);

        solver._spine.IKPositionWeight.ShouldBe(0.75f, 0.0001f);
        solver._spine._tolerance.ShouldBe(0.125f, 0.0001f);
        solver._spine._maxIterations.ShouldBe(11);
        solver._spine._useRotationLimits.ShouldBeTrue();
    }
}
