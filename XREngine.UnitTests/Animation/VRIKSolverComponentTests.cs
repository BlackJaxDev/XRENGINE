using System;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class VRIKSolverComponentTests
{
    private static readonly MethodInfo ApplySampleToTargetsMethod =
        typeof(VRIKSolverComponent).GetMethod("ApplySampleToTargets", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate VRIKSolverComponent.ApplySampleToTargets.");

    [Test]
    public void AssignedHumanoid_TargetProperties_WriteThroughToAssignedHumanoid()
    {
        var humanoidRoot = new SceneNode("HumanoidRoot", new Transform());
        var solverRoot = new SceneNode("SolverRoot", new Transform());
        var targetNode = new SceneNode(humanoidRoot, "HeadTracker", new Transform());
        var humanoid = humanoidRoot.AddComponent<HumanoidComponent>()!;
        var solver = solverRoot.AddComponent<VRIKSolverComponent>()!;

        solver.AssignedHumanoid = humanoid;
        solver.HeadTarget = (targetNode.Transform, Matrix4x4.CreateTranslation(0.0f, 1.0f, 0.0f));

        humanoid.GetIKTarget(EHumanoidIKTarget.Head).tfm.ShouldBeSameAs(targetNode.Transform);
        humanoid.GetIKTarget(EHumanoidIKTarget.Head).offset.Translation.ShouldBe(new Vector3(0.0f, 1.0f, 0.0f));
    }

    [Test]
    public void ClearTargets_ClearsHumanoidOwnedTargets()
    {
        var root = new SceneNode("Root", new Transform());
        var headTarget = new SceneNode(root, "HeadTarget", new Transform());
        var handTarget = new SceneNode(root, "LeftHandTarget", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<VRIKSolverComponent>()!;

        humanoid.SetIKTarget(EHumanoidIKTarget.Head, headTarget.Transform, Matrix4x4.Identity);
        humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, handTarget.Transform, Matrix4x4.Identity);

        solver.ClearTargets();

        humanoid.GetIKTarget(EHumanoidIKTarget.Head).tfm.ShouldBeNull();
        humanoid.GetIKTarget(EHumanoidIKTarget.LeftHand).tfm.ShouldBeNull();
    }

    [Test]
    public void ApplySampleToTargets_RespectsDisabledTargetUpdates()
    {
        var root = new SceneNode("Root", new Transform());
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var solver = root.AddComponent<VRIKSolverComponent>()!;
        var headTargetNode = new SceneNode(root, "HeadTarget", new Transform());
        var leftHandTargetNode = new SceneNode(root, "LeftHandTarget", new Transform());

        humanoid.SetIKTarget(EHumanoidIKTarget.Head, headTargetNode.Transform, Matrix4x4.Identity);
        humanoid.SetIKTarget(EHumanoidIKTarget.LeftHand, leftHandTargetNode.Transform, Matrix4x4.Identity);
        solver.UpdateHeadTarget = false;

        var sample = new HumanoidPoseSample(
            new Vector3(10.0f, 0.0f, 0.0f),
            0.0f,
            Vector3.Zero,
            new Vector3(1.0f, 2.0f, 3.0f),
            new Vector3(4.0f, 5.0f, 6.0f),
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero);

        ApplySampleToTargetsMethod.Invoke(solver, new object[] { sample });

        headTargetNode.Transform.WorldTranslation.ShouldBe(Vector3.Zero);
        leftHandTargetNode.Transform.WorldTranslation.ShouldBe(new Vector3(14.0f, 5.0f, 6.0f));
    }
}