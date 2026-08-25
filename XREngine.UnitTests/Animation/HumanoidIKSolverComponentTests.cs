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

        solver.SetAnimatedHandPosition(new Vector3(1.0f, 2.0f, 3.0f), leftHand: true);
        solver.SetAnimatedHandRotation(Quaternion.Identity, leftHand: true);

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

    [Test]
    public void AnimatedGoalTargets_AreStoredOnHumanoid()
    {
        var root = new SceneNode("Root", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;

        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;

        solver.SetAnimatedHandPosition(new Vector3(1.0f, 2.0f, 3.0f), leftHand: true);
        solver.SetAnimatedHandRotation(Quaternion.Identity, leftHand: true);

        var humanoidTarget = humanoid.GetIKTargetTransform(EHumanoidIKTarget.LeftHand);
        humanoidTarget.ShouldNotBeNull();
        solver._leftHand.TargetIKTransform.ShouldBeSameAs(humanoidTarget);
    }

    [Test]
    public void DisabledAnimatedGoalTarget_DoesNotMoveExistingHumanoidTarget()
    {
        var root = new SceneNode("Root", new Transform());
        var targetNode = new SceneNode(root, "ExistingLeftHandTarget", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;

        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;
        humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, targetNode.GetTransformAs<Transform>(true), Matrix4x4.Identity);
        solver.UpdateLeftHandTarget = false;

        solver.SetAnimatedHandPosition(new Vector3(5.0f, 6.0f, 7.0f), leftHand: true);
        solver.SetAnimatedHandRotation(Quaternion.Identity, leftHand: true);

        targetNode.Transform.WorldTranslation.ShouldBe(Vector3.Zero);
        solver._leftHand.TargetIKTransform.ShouldBeSameAs(targetNode.Transform);
    }

    [Test]
    public void AnimatedGoalPolicy_ReportsIgnoredAndUncalibratedGoalsWithoutCreatingTargets()
    {
        var root = new SceneNode("Root", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;

        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.ApplyIfCalibrated;
        humanoid.Settings.IsIKCalibrated = false;
        solver.SetAnimatedIKPosition(ELimbEndEffector.LeftFoot, new Vector3(1.0f, 2.0f, 3.0f));

        solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftFoot).Status
            .ShouldBe(EHumanoidIKGoalApplicationStatus.SkippedUncalibrated);
        humanoid.GetIKTargetTransform(EHumanoidIKTarget.LeftFoot).ShouldBeNull();

        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.Ignore;
        solver.SetAnimatedIKPosition(ELimbEndEffector.RightHand, new Vector3(4.0f, 5.0f, 6.0f));

        solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.RightHand).Status
            .ShouldBe(EHumanoidIKGoalApplicationStatus.IgnoredByPolicy);
        humanoid.GetIKTargetTransform(EHumanoidIKTarget.RightHand).ShouldBeNull();
    }

    [Test]
    public void ContactCompensation_IsPostPoseAndCanTargetFeetSeparatelyFromHands()
    {
        var root = new SceneNode("Root", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;
        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;

        solver.ConfigureAnimatedGoalContactCompensation(
            EHumanoidContactCompensationMode.GroundPlaneFeet,
            planeHeight: 0.0f,
            clearance: 1.0f,
            weight: 0.5f);
        solver.SetAnimatedIKPosition(ELimbEndEffector.LeftFoot, new Vector3(0.0f, -1.0f, 0.0f));
        solver.SetAnimatedIKPosition(ELimbEndEffector.LeftHand, new Vector3(0.0f, -1.0f, 0.0f));

        HumanoidIKGoalDiagnosticState foot = solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftFoot);
        foot.Status.ShouldBe(EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation);
        foot.AuthoredBodyLocalPosition.ShouldBe(new Vector3(0.0f, -1.0f, 0.0f));
        foot.BodyFrameWorldPosition.ShouldBe(new Vector3(0.0f, -1.0f, 0.0f));
        foot.ContactCompensationOffset.ShouldBe(new Vector3(0.0f, 1.0f, 0.0f));
        foot.FinalWorldPosition.ShouldBe(Vector3.Zero);

        HumanoidIKGoalDiagnosticState hand = solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftHand);
        hand.Status.ShouldBe(EHumanoidIKGoalApplicationStatus.AppliedAuthored);
        hand.ContactCompensationOffset.ShouldBe(Vector3.Zero);
        hand.FinalWorldPosition.ShouldBe(new Vector3(0.0f, -1.0f, 0.0f));

        solver.ConfigureAnimatedGoalContactCompensation(
            EHumanoidContactCompensationMode.GroundPlaneFeetAndHands,
            planeHeight: 0.0f,
            clearance: 1.0f,
            weight: 1.0f);

        hand = solver.GetAnimatedIKGoalDiagnostic(ELimbEndEffector.LeftHand);
        hand.Status.ShouldBe(EHumanoidIKGoalApplicationStatus.AppliedWithContactCompensation);
        hand.ContactCompensationOffset.ShouldBe(new Vector3(0.0f, 2.0f, 0.0f));
        hand.FinalWorldPosition.ShouldBe(new Vector3(0.0f, 1.0f, 0.0f));
    }
}
