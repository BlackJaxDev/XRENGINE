using System;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimStateMachineComponentTests
{
    private static readonly MethodInfo ApplyMusclePoseMethod =
        typeof(HumanoidComponent).GetMethod("ApplyMusclePose", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate HumanoidComponent.ApplyMusclePose.");

    [Test]
    public void Deactivate_HumanoidStateMachine_RestoresBindPoseAndClearsHumanoidMuscles()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));

        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        var stateMachine = root.AddComponent<AnimStateMachineComponent>()!;
        stateMachine.Humanoid = humanoid;

        humanoid.SetValue(EHumanoidValue.LeftArmDownUp, 0.5f);
        InvokePrivate(ApplyMusclePoseMethod, humanoid);

        var leftArmTransform = leftArm.GetTransformAs<Transform>(true)!;
        Quaternion bindRotation = Quaternion.Normalize(leftArmTransform.BindState.Rotation);
        Quaternion posedRotation = Quaternion.Normalize(leftArmTransform.Rotation);
        Quaternion.Dot(posedRotation, bindRotation).ShouldBeLessThan(0.999f);
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeTrue();

        stateMachine.IsActive = false;

        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeFalse();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeFalse();
        AssertEquivalent(Quaternion.Normalize(leftArmTransform.Rotation), bindRotation);
    }

    private static void SaveBindPoseRecursive(SceneNode node)
    {
        node.Transform.SaveBindState();
        foreach (var child in node.Transform.Children)
        {
            if (child.SceneNode is not null)
                SaveBindPoseRecursive(child.SceneNode);
        }
    }

    private static void AssertEquivalent(Quaternion actual, Quaternion expected)
    {
        const float epsilon = 0.0001f;
        if (Quaternion.Dot(actual, expected) < 0.0f)
            actual = new Quaternion(-actual.X, -actual.Y, -actual.Z, -actual.W);

        actual.X.ShouldBe(expected.X, epsilon);
        actual.Y.ShouldBe(expected.Y, epsilon);
        actual.Z.ShouldBe(expected.Z, epsilon);
        actual.W.ShouldBe(expected.W, epsilon);
    }

    private static void InvokePrivate(MethodInfo method, object target)
        => method.Invoke(target, null);
}