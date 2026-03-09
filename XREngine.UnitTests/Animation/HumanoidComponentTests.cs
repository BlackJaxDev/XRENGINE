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
    public void ApplyMusclePose_UsesConfiguredAxisMapping_ForSpine()
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
        var customSpineMapping = new BoneAxisMapping
        {
            TwistAxis = 2,
            TwistSign = 1,
            FrontBackAxis = 1,
            FrontBackSign = 1,
            LeftRightAxis = 0,
            LeftRightSign = 1,
        };
        humanoid.Settings.BoneAxisMappings["Spine"] = customSpineMapping;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.SpineFrontBack, 0.5f);
        InvokeApplyMusclePose(humanoid);

        var spineTransform = spine.GetTransformAs<Transform>(true)!;
        Quaternion expectedRelative = CreateExpectedRotation(customSpineMapping, yawDeg: 0.0f, pitchDeg: 20.0f, rollDeg: 0.0f);
        Quaternion expected = Quaternion.Normalize(spineTransform.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(spineTransform.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ApplyMusclePose_UsesConfiguredAxisMapping_ForUpperLeg()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(0.0f, -0.4f, 0.2f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.0f, -0.4f, -0.2f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(0.0f, 0.1f, 0.25f)));
        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(0.0f, 0.0f, 0.3f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(0.0f, 0.0f, 0.3f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(0.0f, 0.0f, 0.2f)));

        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.0f, 0.1f, -0.25f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.0f, 0.0f, -0.3f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.0f, 0.0f, -0.3f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.0f, 0.0f, -0.2f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        var customUpperLegMapping = new BoneAxisMapping
        {
            TwistAxis = 2,
            TwistSign = 1,
            FrontBackAxis = 1,
            FrontBackSign = -1,
            LeftRightAxis = 0,
            LeftRightSign = 1,
        };
        humanoid.Settings.BoneAxisMappings["LeftLeg"] = customUpperLegMapping;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftUpperLegFrontBack, 0.5f);
        InvokeApplyMusclePose(humanoid);

        float pitchDeg = GetExpectedDeg(humanoid, EHumanoidValue.LeftUpperLegFrontBack, 0.5f);
        Quaternion expectedRelative = CreateExpectedRotation(customUpperLegMapping, yawDeg: 0.0f, pitchDeg: pitchDeg, rollDeg: 0.0f);

        var leftLegTransform = leftLeg.GetTransformAs<Transform>(true)!;
        Quaternion expected = Quaternion.Normalize(leftLegTransform.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(leftLegTransform.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ApplyMusclePose_UsesConfiguredAxisMapping_ForShoulder()
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
        var customShoulderMapping = new BoneAxisMapping
        {
            TwistAxis = 0,
            TwistSign = 1,
            FrontBackAxis = 2,
            FrontBackSign = -1,
            LeftRightAxis = 1,
            LeftRightSign = 1,
        };
        humanoid.Settings.BoneAxisMappings["LeftShoulder"] = customShoulderMapping;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftShoulderDownUp, 0.5f);
        InvokeApplyMusclePose(humanoid);

        float pitchDeg = GetExpectedDeg(humanoid, EHumanoidValue.LeftShoulderDownUp, 0.5f);
        var shoulderTransform = leftShoulder.GetTransformAs<Transform>(true)!;
        Quaternion expectedRelative = CreateExpectedRotation(customShoulderMapping, yawDeg: 0.0f, pitchDeg: pitchDeg, rollDeg: 0.0f);
        Quaternion expected = Quaternion.Normalize(shoulderTransform.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(shoulderTransform.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ApplyMusclePose_UsesConfiguredAxisMapping_ForFoot()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        var leftFoot = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        _ = new SceneNode(leftFoot, "LeftToes", new Transform(translation: new(0.0f, 0.0f, 0.12f)));

        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        var rightFoot = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        _ = new SceneNode(rightFoot, "RightToes", new Transform(translation: new(0.0f, 0.0f, 0.12f)));

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
        var customFootMapping = new BoneAxisMapping
        {
            TwistAxis = 1,
            TwistSign = -1,
            FrontBackAxis = 2,
            FrontBackSign = 1,
            LeftRightAxis = 0,
            LeftRightSign = -1,
        };
        humanoid.Settings.BoneAxisMappings["LeftFoot"] = customFootMapping;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftFootUpDown, 0.5f);
        InvokeApplyMusclePose(humanoid);

        float pitchDeg = GetExpectedDeg(humanoid, EHumanoidValue.LeftFootUpDown, 0.5f);
        var footTransform = leftFoot.GetTransformAs<Transform>(true)!;
        Quaternion expectedRelative = CreateExpectedRotation(customFootMapping, yawDeg: 0.0f, pitchDeg: pitchDeg, rollDeg: 0.0f);
        Quaternion expected = Quaternion.Normalize(footTransform.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(footTransform.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void SetFromNode_AutoProfile_UsesStableSwingSigns_ForMirroredUpperLegs()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.01f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(-0.01f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        BoneAxisMapping left = humanoid.Settings.BoneAxisMappings["LeftLeg"];
        BoneAxisMapping right = humanoid.Settings.BoneAxisMappings["RightLeg"];

        left.FrontBackAxis.ShouldBe(right.FrontBackAxis);
        left.LeftRightAxis.ShouldBe(right.LeftRightAxis);
        Math.Abs(left.FrontBackSign).ShouldBe(1);
        Math.Abs(right.FrontBackSign).ShouldBe(1);
        Math.Abs(left.LeftRightSign).ShouldBe(1);
        Math.Abs(right.LeftRightSign).ShouldBe(1);
    }

    [Test]
    public void ApplyMusclePose_AutoProfile_UsesBodyBasis_ForUpperLegFrontBack()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(0.0f, -0.4f, 0.2f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.0f, -0.4f, -0.2f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(0.0f, 0.1f, 0.25f)));
        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(0.0f, 0.0f, 0.3f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(0.0f, 0.0f, 0.3f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(0.0f, 0.0f, 0.2f)));

        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.0f, 0.1f, -0.25f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.0f, 0.0f, -0.3f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.0f, 0.0f, -0.3f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.0f, 0.0f, -0.2f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftUpperLegFrontBack, 0.5f);
        InvokeApplyMusclePose(humanoid);

        GetBindBodyBasis(humanoid, out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);
        Quaternion expectedRelative = CreateExpectedBodyBasisRotation(
            leftLeg,
            yawDeg: 0.0f,
            pitchDeg: GetExpectedDeg(humanoid, EHumanoidValue.LeftUpperLegFrontBack, 0.5f),
            rollDeg: 0.0f,
            twistAxisWorld: GetBoneDirectionWorld(leftLeg, leftKnee, -bodyUp),
            pitchAxisWorld: -bodyLeft,
            rollAxisWorld: -bodyForward);

        Quaternion expected = Quaternion.Normalize(leftLeg.GetTransformAs<Transform>(true)!.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(leftLeg.GetTransformAs<Transform>(true)!.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ApplyMusclePose_AutoProfile_UsesBodyBasis_ForShoulderDownUp()
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

        humanoid.SetValue(EHumanoidValue.LeftShoulderDownUp, 0.5f);
        InvokeApplyMusclePose(humanoid);

        GetBindBodyBasis(humanoid, out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward);
        Quaternion expectedRelative = CreateExpectedBodyBasisRotation(
            leftShoulder,
            yawDeg: 0.0f,
            pitchDeg: GetExpectedDeg(humanoid, EHumanoidValue.LeftShoulderDownUp, 0.5f),
            rollDeg: 0.0f,
            twistAxisWorld: GetBoneDirectionWorld(leftShoulder, leftArm, bodyLeft),
            pitchAxisWorld: bodyForward,
            rollAxisWorld: -bodyUp);

        Quaternion expected = Quaternion.Normalize(leftShoulder.GetTransformAs<Transform>(true)!.BindState.Rotation * expectedRelative);
        Quaternion actual = Quaternion.Normalize(leftShoulder.GetTransformAs<Transform>(true)!.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ApplyMusclePose_AutoProfile_RotatesMirroredUpperLegsConsistently()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.01f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(-0.01f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        humanoid.SetValue(EHumanoidValue.LeftUpperLegFrontBack, 0.5f);
        humanoid.SetValue(EHumanoidValue.RightUpperLegFrontBack, 0.5f);
        InvokeApplyMusclePose(humanoid);

        Quaternion leftRotation = Quaternion.Normalize(leftLeg.GetTransformAs<Transform>(true)!.Rotation);
        Quaternion rightRotation = Quaternion.Normalize(rightLeg.GetTransformAs<Transform>(true)!.Rotation);

        AssertEquivalent(leftRotation, rightRotation);
    }

    [Test]
    public void SetFromNode_RebuildsAutoGeneratedAxisMappings()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.01f, -0.35f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(-0.01f, -0.35f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Settings.ProfileSource = "auto-generated";
        var staleMapping = new BoneAxisMapping
        {
            TwistAxis = 0,
            TwistSign = -1,
            FrontBackAxis = 2,
            FrontBackSign = -1,
            LeftRightAxis = 1,
            LeftRightSign = -1,
        };
        humanoid.Settings.BoneAxisMappings["LeftLeg"] = staleMapping;

        humanoid.SetFromNode();

        BoneAxisMapping mapping = humanoid.Settings.BoneAxisMappings["LeftLeg"];
        mapping.ShouldNotBe(staleMapping);
    }

    [Test]
    public void SetFromNode_PreservesManualAxisMappings()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.01f, -0.35f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        _ = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(-0.01f, -0.35f, 0.0f)));

        SaveBindPoseRecursive(root);

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Settings.ProfileSource = "manual";
        var manualMapping = new BoneAxisMapping
        {
            TwistAxis = 0,
            TwistSign = -1,
            FrontBackAxis = 2,
            FrontBackSign = -1,
            LeftRightAxis = 1,
            LeftRightSign = -1,
        };
        humanoid.Settings.BoneAxisMappings["LeftLeg"] = manualMapping;

        humanoid.SetFromNode();

        humanoid.Settings.BoneAxisMappings["LeftLeg"].ShouldBe(manualMapping);
    }

    [Test]
    public void ApplyNeutralPoseFromAuditReport_LoadsPerBoneBindRelativeOffsets()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        Quaternion expectedOffset = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 15.0f * MathF.PI / 180.0f));
        var report = new HumanoidPoseAuditReport
        {
            DefaultMusclePose = new HumanoidPoseAuditSample
            {
                Bones =
                [
                    new HumanoidPoseAuditBoneSample
                    {
                        Name = "Spine",
                        BindRelativeRotation = HumanoidPoseAuditQuaternion.From(expectedOffset),
                    },
                ],
            },
        };

        humanoid.ApplyNeutralPoseFromAuditReport(report);

        humanoid.Settings.TryGetNeutralPoseBoneRotation("Spine", out Quaternion actualOffset).ShouldBeTrue();
        AssertEquivalent(Quaternion.Normalize(actualOffset), expectedOffset);
    }

    [Test]
    public void ApplyMusclePose_ZeroMuscle_UsesNeutralPoseOffsetAsBase()
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

        Quaternion neutralOffset = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 12.0f * MathF.PI / 180.0f));
        humanoid.Settings.NeutralPoseBoneRotations["Spine"] = neutralOffset;
        humanoid.SetValue(EHumanoidValue.SpineFrontBack, 0.0f);

        InvokeApplyMusclePose(humanoid);

        var spineTransform = spine.GetTransformAs<Transform>(true)!;
        Quaternion expected = Quaternion.Normalize(spineTransform.BindState.Rotation * neutralOffset);
        Quaternion actual = Quaternion.Normalize(spineTransform.Rotation);

        AssertEquivalent(actual, expected);
    }

    [Test]
    public void ResetPose_ClearsStoredMuscles_AndKeepsBindPoseOnNextApply()
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

        humanoid.SetValue(EHumanoidValue.SpineFrontBack, 0.5f);
        InvokeApplyMusclePose(humanoid);

        var spineTransform = spine.GetTransformAs<Transform>(true)!;
        Quaternion posedRotation = Quaternion.Normalize(spineTransform.Rotation);
        Quaternion bindRotation = Quaternion.Normalize(spineTransform.BindState.Rotation);
        Quaternion.Dot(posedRotation, bindRotation).ShouldBeLessThan(0.999f);

        humanoid.ResetPose();

        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out _).ShouldBeFalse();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.SpineFrontBack, out _).ShouldBeFalse();
        humanoid.Settings.CurrentValues.ContainsKey(EHumanoidValue.SpineFrontBack).ShouldBeFalse();

        InvokeApplyMusclePose(humanoid);

        Quaternion resetRotation = Quaternion.Normalize(spineTransform.Rotation);
        AssertEquivalent(resetRotation, bindRotation);
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

    private static float GetExpectedDeg(HumanoidComponent humanoid, EHumanoidValue value, float muscle)
    {
        Vector2 range = humanoid.Settings.GetResolvedMuscleRotationDegRange(value);
        return muscle >= 0.0f
            ? muscle * range.Y
            : -muscle * range.X;
    }

    private static Quaternion CreateExpectedBodyBasisRotation(
        SceneNode node,
        float yawDeg,
        float pitchDeg,
        float rollDeg,
        Vector3 twistAxisWorld,
        Vector3 pitchAxisWorld,
        Vector3 rollAxisWorld)
    {
        const float degToRad = MathF.PI / 180.0f;

        Vector3 twistLocal = TransformWorldAxisToBoneLocal(node, twistAxisWorld);
        Vector3 pitchLocal = TransformWorldAxisToBoneLocal(node, pitchAxisWorld);
        Vector3 rollLocal = TransformWorldAxisToBoneLocal(node, rollAxisWorld);

        pitchLocal -= Vector3.Dot(pitchLocal, twistLocal) * twistLocal;
        float pitchLen = pitchLocal.LengthSquared();
        if (pitchLen > 1e-8f)
        {
            pitchLocal /= MathF.Sqrt(pitchLen);
            rollLocal = Vector3.Cross(twistLocal, pitchLocal);
        }
        else
        {
            rollLocal -= Vector3.Dot(rollLocal, twistLocal) * twistLocal;
            float rollLen = rollLocal.LengthSquared();
            if (rollLen > 1e-8f)
            {
                rollLocal /= MathF.Sqrt(rollLen);
                pitchLocal = Vector3.Cross(rollLocal, twistLocal);
            }
        }

        Quaternion twist = Quaternion.CreateFromAxisAngle(twistLocal, -yawDeg * degToRad);
        Quaternion pitch = Quaternion.CreateFromAxisAngle(pitchLocal, -pitchDeg * degToRad);
        Quaternion roll = Quaternion.CreateFromAxisAngle(rollLocal, -rollDeg * degToRad);
        return Quaternion.Normalize(roll * pitch * twist);
    }

    private static Vector3 TransformWorldAxisToBoneLocal(SceneNode node, Vector3 worldAxis)
    {
        Matrix4x4.Invert(node.Transform.BindMatrix, out Matrix4x4 invBind).ShouldBeTrue();
        Vector3 local = Vector3.TransformNormal(worldAxis, invBind);
        float lenSq = local.LengthSquared();
        return lenSq > 1e-8f ? local / MathF.Sqrt(lenSq) : worldAxis;
    }

    private static Vector3 GetBoneDirectionWorld(SceneNode bone, SceneNode child, Vector3 fallback)
    {
        Vector3 dir = child.Transform.BindMatrix.Translation - bone.Transform.BindMatrix.Translation;
        float lenSq = dir.LengthSquared();
        return lenSq > 1e-8f ? dir / MathF.Sqrt(lenSq) : fallback;
    }

    private static void GetBindBodyBasis(HumanoidComponent humanoid, out Vector3 bodyLeft, out Vector3 bodyUp, out Vector3 bodyForward)
    {
        Vector3 hipsPos = humanoid.Hips.WorldBindPose.Translation;
        Vector3 spinePos = humanoid.Spine.WorldBindPose.Translation;

        bodyUp = NormalizeOrFallback(spinePos - hipsPos, Vector3.UnitY);

        Vector3 sideSum =
            GetBindSideDelta(humanoid.Left.Shoulder, humanoid.Right.Shoulder) +
            GetBindSideDelta(humanoid.Left.Arm, humanoid.Right.Arm) +
            GetBindSideDelta(humanoid.Left.Wrist, humanoid.Right.Wrist) +
            GetBindSideDelta(humanoid.Left.Leg, humanoid.Right.Leg) +
            GetBindSideDelta(humanoid.Left.Foot, humanoid.Right.Foot);

        bodyLeft = NormalizeOrFallback(RejectAxis(sideSum, bodyUp), Vector3.UnitX);
        bodyForward = NormalizeOrFallback(Vector3.Cross(bodyLeft, bodyUp), Vector3.UnitZ);
        bodyLeft = NormalizeOrFallback(Vector3.Cross(bodyUp, bodyForward), bodyLeft);
    }

    private static Vector3 GetBindSideDelta(HumanoidComponent.BoneDef left, HumanoidComponent.BoneDef right)
        => left.WorldBindPose.Translation - right.WorldBindPose.Translation;

    private static Vector3 RejectAxis(Vector3 vector, Vector3 normal)
        => vector - Vector3.Dot(vector, normal) * normal;

    private static Vector3 NormalizeOrFallback(Vector3 vector, Vector3 fallback)
    {
        float lenSq = vector.LengthSquared();
        return lenSq > 1e-8f ? vector / MathF.Sqrt(lenSq) : fallback;
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
