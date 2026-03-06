using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidComponentTests
{
    [Test]
    public void SetFromNode_AssignsNegativeXBonesToLeftSide_AndInitialHandTargetsMatch()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        _ = new SceneNode(hips, "Spine", new Transform(translation: new(-0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(hips.FindDescendantByName("Spine")!, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var head = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));

        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));

        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));

        var leftHand = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));
        var rightHand = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        var leftEye = new SceneNode(head, "LeftEye", new Transform(translation: new(-0.05f, 0.02f, 0.08f)));
        var rightEye = new SceneNode(head, "RightEye", new Transform(translation: new(0.05f, 0.02f, 0.08f)));

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        humanoid.Left.Leg.Node.ShouldBeSameAs(leftLeg);
        humanoid.Right.Leg.Node.ShouldBeSameAs(rightLeg);
        humanoid.Left.Shoulder.Node.ShouldBeSameAs(leftShoulder);
        humanoid.Right.Shoulder.Node.ShouldBeSameAs(rightShoulder);
        humanoid.Left.Wrist.Node.ShouldBeSameAs(leftHand);
        humanoid.Right.Wrist.Node.ShouldBeSameAs(rightHand);
        humanoid.Left.Eye.Node.ShouldBeSameAs(leftEye);
        humanoid.Right.Eye.Node.ShouldBeSameAs(rightEye);
        humanoid.LeftHandTarget.tfm.ShouldBeSameAs(leftHand.Transform);
        humanoid.RightHandTarget.tfm.ShouldBeSameAs(rightHand.Transform);
    }
}
