using System.Numerics;
using System.Reflection;
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

    [Test]
    public void SetFromNode_DetectsFingerChainsAcrossNestedBones_AndCommonAliases()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));

        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));

        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));

        var leftHand = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));
        var rightHand = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        var leftIndexMetacarpal = new SceneNode(leftHand, "LeftIndexMetacarpal", new Transform(translation: new(-0.03f, 0.0f, 0.05f)));
        var leftIndexProximal = new SceneNode(leftIndexMetacarpal, "LeftIndexProximal", new Transform(translation: new(-0.01f, 0.0f, 0.03f)));
        var leftIndexIntermediate = new SceneNode(leftIndexProximal, "LeftIndexIntermediate", new Transform(translation: new(0.0f, 0.0f, 0.02f)));
        var leftIndexDistal = new SceneNode(leftIndexIntermediate, "LeftIndexDistal", new Transform(translation: new(0.0f, 0.0f, 0.02f)));

        var leftLittleProximal = new SceneNode(leftHand, "LeftLittleProximal", new Transform(translation: new(-0.05f, -0.01f, 0.04f)));
        var leftLittleIntermediate = new SceneNode(leftLittleProximal, "LeftLittleIntermediate", new Transform(translation: new(0.0f, 0.0f, 0.02f)));
        var leftLittleDistal = new SceneNode(leftLittleIntermediate, "LeftLittleDistal", new Transform(translation: new(0.0f, 0.0f, 0.02f)));

        var rightThumb01 = new SceneNode(rightHand, "RightThumb01", new Transform(translation: new(0.03f, 0.0f, 0.04f)));
        var rightThumb02 = new SceneNode(rightThumb01, "RightThumb02", new Transform(translation: new(0.0f, 0.0f, 0.02f)));
        var rightThumb03 = new SceneNode(rightThumb02, "RightThumb03", new Transform(translation: new(0.0f, 0.0f, 0.02f)));

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        humanoid.Left.Hand.Index.Proximal.Node.ShouldBeSameAs(leftIndexProximal);
        humanoid.Left.Hand.Index.Intermediate.Node.ShouldBeSameAs(leftIndexIntermediate);
        humanoid.Left.Hand.Index.Distal.Node.ShouldBeSameAs(leftIndexDistal);

        humanoid.Left.Hand.Pinky.Proximal.Node.ShouldBeSameAs(leftLittleProximal);
        humanoid.Left.Hand.Pinky.Intermediate.Node.ShouldBeSameAs(leftLittleIntermediate);
        humanoid.Left.Hand.Pinky.Distal.Node.ShouldBeSameAs(leftLittleDistal);

        humanoid.Right.Hand.Thumb.Proximal.Node.ShouldBeSameAs(rightThumb01);
        humanoid.Right.Hand.Thumb.Intermediate.Node.ShouldBeSameAs(rightThumb02);
        humanoid.Right.Hand.Thumb.Distal.Node.ShouldBeSameAs(rightThumb03);
    }

    [Test]
    public void SetFromNode_PrefersExplicitSideNames_WhenXAxisIsMirrored()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var head = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "Leg.L", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightLeg = new SceneNode(hips, "Leg.R", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "Shoulder.L", new Transform(translation: new(0.25f, 0.1f, 0.0f)));
        var rightShoulder = new SceneNode(chest, "Shoulder.R", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));

        var leftArm = new SceneNode(leftShoulder, "Arm.L", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "Arm.R", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));

        var leftElbow = new SceneNode(leftArm, "Elbow.L", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "Elbow.R", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));

        var leftHand = new SceneNode(leftElbow, "Wrist.L", new Transform(translation: new(0.2f, 0.0f, 0.0f)));
        var rightHand = new SceneNode(rightElbow, "Wrist.R", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));

        var leftFoot = new SceneNode(leftLeg, "Foot.L", new Transform(translation: new(0.0f, -0.3f, 0.0f)));
        var rightFoot = new SceneNode(rightLeg, "Foot.R", new Transform(translation: new(0.0f, -0.3f, 0.0f)));

        var leftEye = new SceneNode(head, "Eye.L", new Transform(translation: new(0.05f, 0.02f, 0.08f)));
        var rightEye = new SceneNode(head, "Eye.R", new Transform(translation: new(-0.05f, 0.02f, 0.08f)));

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        humanoid.Left.Leg.Node.ShouldBeSameAs(leftLeg);
        humanoid.Right.Leg.Node.ShouldBeSameAs(rightLeg);
        humanoid.Left.Shoulder.Node.ShouldBeSameAs(leftShoulder);
        humanoid.Right.Shoulder.Node.ShouldBeSameAs(rightShoulder);
        humanoid.Left.Wrist.Node.ShouldBeSameAs(leftHand);
        humanoid.Right.Wrist.Node.ShouldBeSameAs(rightHand);
        humanoid.Left.Foot.Node.ShouldBeSameAs(leftFoot);
        humanoid.Right.Foot.Node.ShouldBeSameAs(rightFoot);
        humanoid.Left.Eye.Node.ShouldBeSameAs(leftEye);
        humanoid.Right.Eye.Node.ShouldBeSameAs(rightEye);
        humanoid.LeftHandTarget.tfm.ShouldBeSameAs(leftHand.Transform);
        humanoid.RightHandTarget.tfm.ShouldBeSameAs(rightHand.Transform);
        humanoid.LeftFootTarget.tfm.ShouldBeSameAs(leftFoot.Transform);
        humanoid.RightFootTarget.tfm.ShouldBeSameAs(rightFoot.Transform);
    }

    [Test]
    public void ApplyMusclePose_UsesConfiguredAxisMapping_ForUpperArm()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        _ = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));

        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));

        var rightArm = new SceneNode(chest.FindDescendantByName("RightShoulder")!, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var customArmMapping = new BoneAxisMapping
        {
            TwistAxis = 2,
            TwistSign = 1,
            FrontBackAxis = 1,
            FrontBackSign = 1,
            LeftRightAxis = 0,
            LeftRightSign = 1,
        };
        humanoid.Settings.BoneAxisMappings["LeftArm"] = customArmMapping;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftArmDownUp, 0.5f);
        InvokeApplyMusclePose(humanoid);

        var leftArmTransform = leftArm.GetTransformAs<Transform>(true)!;
        Quaternion expectedRelative = CreateExpectedRotation(customArmMapping, yawDeg: 0.0f, pitchDeg: 50.0f, rollDeg: 0.0f);
        Quaternion expected = Quaternion.Normalize(leftArmTransform.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(leftArmTransform.Rotation);

        AssertEquivalent(actual, expected);
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

    private static void InvokeApplyMusclePose(HumanoidComponent humanoid)
    {
        var method = typeof(HumanoidComponent).GetMethod("ApplyMusclePose", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();
        method.Invoke(humanoid, null);
    }

    private static Quaternion CreateExpectedRotation(BoneAxisMapping mapping, float yawDeg, float pitchDeg, float rollDeg)
    {
        const float degToRad = MathF.PI / 180.0f;

        Quaternion twist = Quaternion.CreateFromAxisAngle(
            AxisIndexToVector(mapping.TwistAxis),
            HandednessSign(mapping.TwistAxis) * mapping.TwistSign * yawDeg * degToRad);
        Quaternion frontBack = Quaternion.CreateFromAxisAngle(
            AxisIndexToVector(mapping.FrontBackAxis),
            HandednessSign(mapping.FrontBackAxis) * mapping.FrontBackSign * pitchDeg * degToRad);
        Quaternion leftRight = Quaternion.CreateFromAxisAngle(
            AxisIndexToVector(mapping.LeftRightAxis),
            HandednessSign(mapping.LeftRightAxis) * mapping.LeftRightSign * rollDeg * degToRad);

        return Quaternion.Normalize(leftRight * frontBack * twist);
    }

    private static Vector3 AxisIndexToVector(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        2 => Vector3.UnitZ,
        _ => Vector3.UnitY,
    };

    private static float HandednessSign(int axis)
        => axis == 2 ? 1.0f : -1.0f;

    private static void AssertEquivalent(Quaternion actual, Quaternion expected, float tolerance = 0.0001f)
    {
        if (Quaternion.Dot(actual, expected) < 0.0f)
            actual = new Quaternion(-actual.X, -actual.Y, -actual.Z, -actual.W);

        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
        actual.W.ShouldBe(expected.W, tolerance);
    }

}
