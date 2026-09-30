using System.Numerics;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
[NonParallelizable]
public sealed class VrBodyMeasurementTests
{
    [Test]
    public void Settings_PersistUnsetValuesAndExplicitMeasurementMode()
    {
        var unset = new UserSettings();
        unset.PlayerHeight.ShouldBeNull();
        unset.PlayerArmSpan.ShouldBeNull();
        var settings = new UserSettings { BodyMeasurementMode = EVrBodyMeasurementMode.ArmSpan, PlayerHeight = 1.8f, PlayerArmSpan = 1.95f };
        var restored = MemoryPackSerializer.Deserialize<UserSettings>(MemoryPackSerializer.Serialize(settings))!;
        restored.BodyMeasurementMode.ShouldBe(EVrBodyMeasurementMode.ArmSpan);
        restored.PlayerHeight.ShouldBe(1.8f);
        restored.PlayerArmSpan.ShouldBe(1.95f);
    }

    [Test]
    public void Settings_HeightAndArmSpanUseExplicitDifferentRatios()
    {
        var measurements = new AvatarBodyMeasurements(1.6f, 1.8f, false, null);
        var settings = new UserSettings { PlayerHeight = 1.8f, PlayerArmSpan = 2.0f, StandingHeightToEyeHeightRatio = 0.9f };
        VrBodyScaleCalculation.TryCompute(settings, measurements, out float heightScale, out _).ShouldBeTrue();
        heightScale.ShouldBe(1.8f * 0.9f / 1.6f, 0.00001f);
        settings.BodyMeasurementMode = EVrBodyMeasurementMode.ArmSpan;
        VrBodyScaleCalculation.TryCompute(settings, measurements, out float armScale, out _).ShouldBeTrue();
        armScale.ShouldBe(2.0f / 1.8f, 0.00001f);
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(-1.0f)]
    [TestCase(0.0f)]
    [TestCase(4.0f)]
    public void Settings_InvalidValuesDoNotProduceScale(float value)
    {
        var settings = new UserSettings { PlayerHeight = value };
        VrBodyScaleCalculation.TryCompute(settings, new AvatarBodyMeasurements(1.7f, 1.9f, false, null), out _, out string notice).ShouldBeFalse();
        notice.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public void Settings_UnsetAndInvalidAvatarMeasurementsAreExplicitFailures()
    {
        VrBodyScaleCalculation.TryCompute(new UserSettings(), new AvatarBodyMeasurements(1.7f, 1.9f, false, null), out _, out string notice).ShouldBeFalse();
        notice.ShouldBe(VrBodyScaleCalculation.UnsetNotice);
        VrBodyScaleCalculation.TryCompute(new UserSettings { PlayerHeight = 1.8f }, default, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void HeightMismatch_ProducesWarningWithoutChangingPlayerValue()
    {
        var settings = new UserSettings { PlayerHeight = 1.8f };
        VrBodyScaleCalculation.GetCaptureHeightWarning(settings, 1.8f * 0.936f).ShouldBeNull();
        VrBodyScaleCalculation.GetCaptureHeightWarning(settings, 1.1f).ShouldNotBeNullOrEmpty();
        settings.PlayerHeight.ShouldBe(1.8f);
    }

    [Test]
    public void AvatarMeasurements_DoNotRescaleMetricEyeSeparation()
    {
        var state = new RuntimeVrState { EmulatedRenderActive = true };
        state.ScaledIPD.ShouldBe(0.064f, 0.000001f);
        state.RealWorldHeight = 2.5f;
        state.ModelHeight = 0.2f;
        state.DesiredAvatarHeight = 4.0f;
        state.ScaledIPD.ShouldBe(0.064f, 0.000001f);
        state.IPDScalar = 1.05f;
        state.ScaledIPD.ShouldBe(0.064f * 1.05f, 0.000001f);
    }

    [Test]
    public void CanonicalPose_StraightensImportedAPoseAndMeasuresActualFingertips()
    {
        var (root, humanoid) = CreateAvatar(withFingerTips: true, aPose: true);
        try
        {
            humanoid.TryGetCanonicalBodyMeasurements(Vector3.Zero, out AvatarBodyMeasurements before).ShouldBeTrue();
            before.ArmSpan.ShouldBe(1.96f, 0.0001f);
            before.UsesEstimatedHandLength.ShouldBeFalse();
            Vector3 scale = ((Transform)root.Transform).Scale;
            humanoid.SetCanonicalCalibrationPose().ShouldBeTrue();
            Vector3 upper = Vector3.Normalize(humanoid.Left.Elbow.Node!.Transform.WorldTranslation - humanoid.Left.Arm.Node!.Transform.WorldTranslation);
            Vector3 lower = Vector3.Normalize(humanoid.Left.Wrist.Node!.Transform.WorldTranslation - humanoid.Left.Elbow.Node.Transform.WorldTranslation);
            Vector3.Dot(upper, -Vector3.UnitX).ShouldBeGreaterThan(0.9999f);
            Vector3.Dot(lower, -Vector3.UnitX).ShouldBeGreaterThan(0.9999f);
            ((Transform)root.Transform).Scale.ShouldBe(scale);
            humanoid.TryGetCanonicalBodyMeasurements(Vector3.Zero, out AvatarBodyMeasurements after).ShouldBeTrue();
            after.ShouldBe(before);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void MissingFingers_ReportEstimatedHandLength()
    {
        var (root, humanoid) = CreateAvatar(withFingerTips: false, aPose: false);
        try
        {
            humanoid.TryGetCanonicalBodyMeasurements(Vector3.Zero, out AvatarBodyMeasurements measurements).ShouldBeTrue();
            measurements.UsesEstimatedHandLength.ShouldBeTrue();
            measurements.Notice.ShouldNotBeNullOrEmpty();
            measurements.ArmSpan.ShouldBe(1.6f + measurements.EyeHeight * 0.22f, 0.0001f);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ScaleOwner_RescalesAbsolutelyWithoutChangingTrackingParentOrCompounding()
    {
        var (root, humanoid) = CreateAvatar(withFingerTips: true, aPose: true);
        var playspace = new SceneNode("Metric tracking basis", new Transform(translation: new Vector3(5, 2, -3)));
        root.Transform.SetParent(playspace.Transform, false, EParentAssignmentMode.Immediate);
        try
        {
            Matrix4x4 trackingBasis = playspace.Transform.LocalMatrix;
            var component = root.AddComponent<VRHeightScaleComponent>()!;
            component.HumanoidComponent = humanoid;
            Vector3 originalScale = ((Transform)root.Transform).Scale;
            component.PlayerSettings = new UserSettings();
            component.TryApplyPlayerMeasurements(out string unset).ShouldBeFalse();
            unset.ShouldBe(VrBodyScaleCalculation.UnsetNotice);
            ((Transform)root.Transform).Scale.ShouldBe(originalScale);
            var settings = new UserSettings { BodyMeasurementMode = EVrBodyMeasurementMode.ArmSpan, PlayerArmSpan = 1.96f * 1.25f };
            component.PlayerSettings = settings;
            ((IRuntimeVrHeightScaleComponent)component).TryApplyPlayerMeasurements(out _).ShouldBeTrue();
            for (int i = 0; i < 5; i++) component.TryApplyPlayerMeasurements(out _).ShouldBeTrue();
            ((Transform)root.Transform).Scale.X.ShouldBe(1.25f, 0.0001f);
            settings.PlayerArmSpan = 1.96f * 0.9f;
            // A settings/UI event only queues work; the scene owner applies it.
            ((Transform)root.Transform).Scale.X.ShouldBe(1.25f, 0.0001f);
            component.TryApplyPlayerMeasurements(out _).ShouldBeTrue();
            ((Transform)root.Transform).Scale.X.ShouldBe(0.9f, 0.0001f);
            playspace.Transform.LocalMatrix.ShouldBe(trackingBasis);
            settings.PlayerArmSpan = float.NaN;
            component.TryApplyPlayerMeasurements(out _).ShouldBeFalse();
            ((Transform)root.Transform).Scale.X.ShouldBe(0.9f, 0.0001f);
        }
        finally { playspace.Destroy(true); }
    }

    private static (SceneNode Root, HumanoidComponent Humanoid) CreateAvatar(bool withFingerTips, bool aPose)
    {
        var root = new SceneNode("Measurement avatar", new Transform());
        var hips = Bone(root, "Hips", new Vector3(0, 1.0f, 0));
        var spine = Bone(hips, "Spine", new Vector3(0, 0.25f, 0));
        var chest = Bone(spine, "Chest", new Vector3(0, 0.15f, 0));
        var neck = Bone(chest, "Neck", new Vector3(0, 0.2f, 0));
        var head = Bone(neck, "Head", new Vector3(0, 0.1f, 0));
        var left = MakeArm(chest, "Left", -1, withFingerTips, aPose);
        var right = MakeArm(chest, "Right", 1, withFingerTips, aPose);
        var leftLeg = MakeLeg(hips, "Left", -1);
        var rightLeg = MakeLeg(hips, "Right", 1);
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Hips.Node = hips; humanoid.Spine.Node = spine; humanoid.Chest.Node = chest;
        humanoid.Neck.Node = neck; humanoid.Head.Node = head;
        Map(humanoid.Left, left); Map(humanoid.Right, right);
        MapLeg(humanoid.Left, leftLeg); MapLeg(humanoid.Right, rightLeg);
        return (root, humanoid);
    }

    private static SceneNode[] MakeArm(SceneNode chest, string side, int sign, bool fingers, bool aPose)
    {
        var shoulder = Bone(chest, side + "Shoulder", new Vector3(sign * 0.1f, 0.1f, 0));
        var arm = Bone(shoulder, side + "UpperArm", new Vector3(sign * 0.1f, 0, 0));
        if (aPose)
        {
            ((Transform)arm.Transform).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sign * MathF.PI / 4);
            arm.Transform.SaveBindState();
        }
        var elbow = Bone(arm, side + "LowerArm", new Vector3(sign * 0.3f, 0, 0));
        var wrist = Bone(elbow, side + "Hand", new Vector3(sign * 0.3f, 0, 0));
        if (!fingers) return [shoulder, arm, elbow, wrist];
        var proximal = Bone(wrist, side + "MiddleProximal", new Vector3(sign * 0.08f, 0, 0));
        var intermediate = Bone(proximal, side + "MiddleIntermediate", new Vector3(sign * 0.05f, 0, 0));
        var distal = Bone(intermediate, side + "MiddleDistal", new Vector3(sign * 0.03f, 0, 0));
        Bone(distal, side + "MiddleTip", new Vector3(sign * 0.02f, 0, 0));
        return [shoulder, arm, elbow, wrist, proximal, intermediate, distal];
    }

    private static void Map(HumanoidComponent.BodySide side, SceneNode[] nodes)
    {
        side.Shoulder.Node = nodes[0]; side.Arm.Node = nodes[1]; side.Elbow.Node = nodes[2]; side.Wrist.Node = nodes[3];
        if (nodes.Length > 4)
        {
            side.Hand.Middle.Proximal.Node = nodes[4]; side.Hand.Middle.Intermediate.Node = nodes[5]; side.Hand.Middle.Distal.Node = nodes[6];
        }
    }

    private static SceneNode[] MakeLeg(SceneNode hips, string side, int sign)
    {
        var leg = Bone(hips, side + "UpperLeg", new Vector3(sign * 0.1f, 0, 0));
        var knee = Bone(leg, side + "LowerLeg", new Vector3(0, -0.5f, 0));
        var foot = Bone(knee, side + "Foot", new Vector3(0, -0.45f, 0));
        var toes = Bone(foot, side + "Toes", new Vector3(0, 0, -0.15f));
        return [leg, knee, foot, toes];
    }

    private static void MapLeg(HumanoidComponent.BodySide side, SceneNode[] nodes)
    {
        side.Leg.Node = nodes[0]; side.Knee.Node = nodes[1]; side.Foot.Node = nodes[2]; side.Toes.Node = nodes[3];
    }

    private static SceneNode Bone(SceneNode parent, string name, Vector3 translation)
    {
        var bone = new SceneNode(parent, name, new Transform(translation: translation));
        bone.Transform.SaveBindState();
        bone.Transform.RecalculateMatrices(true);
        return bone;
    }
}
