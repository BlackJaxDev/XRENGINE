using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using Unity;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class UnityAnimImporterTests
{
    [Test]
    public void Import_RecordsClipSampleRateMetadata()
    {
        const string yaml = """
AnimationClip:
  m_Name: SampleRateClip
  m_SampleRate: 72
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_FloatCurves: []
""";

        AnimationClip clip = ImportClip(yaml);

        clip.SampleRate.ShouldBe(72);
        clip.LengthInSeconds.ShouldBe(1.0f);
    }

    [Test]
    public void Import_ScalarCurves_RoutePerComponent_PreserveTangents_AndConvertAxes()
    {
        const string yaml = """
AnimationClip:
  m_Name: ScalarClip
  m_SampleRate: 60
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_FloatCurves:
    - path: Hips
      attribute: m_LocalScale.x
      classID: 4
      curve:
        m_Curve:
          - time: 0
            value: 1
            inSlope: -2
            outSlope: 3
            tangentMode: 1
          - time: 1
            value: 2
            inSlope: 4
            outSlope: -5
            tangentMode: 1
    - path: Hips
      attribute: m_LocalPosition.z
      classID: 4
      curve:
        m_Curve:
          - time: 0
            value: 0.5
            inSlope: 1.25
            outSlope: -2.5
            tangentMode: 1
    - path: Hips
      attribute: m_LocalRotation.x
      classID: 4
      curve:
        m_Curve:
          - time: 0
            value: 0.25
            inSlope: -0.75
            outSlope: 1.5
            tangentMode: 1
    - path: ''
      attribute: RootT.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.4
            inSlope: -0.25
            outSlope: 0.5
            tangentMode: 1
    - path: ''
      attribute: RootT.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.75
            inSlope: -1
            outSlope: 2
            tangentMode: 1
    - path: ''
      attribute: RootQ.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.15
            inSlope: -0.3
            outSlope: 0.45
            tangentMode: 1
    - path: ''
      attribute: RootQ.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.2
            inSlope: 0.4
            outSlope: -0.8
            tangentMode: 1
    - path: ''
      attribute: LeftFootT.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.6
            inSlope: -0.7
            outSlope: 0.8
            tangentMode: 1
    - path: ''
      attribute: LeftFootT.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.9
            inSlope: -1.1
            outSlope: 1.2
            tangentMode: 1
    - path: ''
      attribute: LeftFootQ.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.35
            inSlope: -0.5
            outSlope: 0.7
            tangentMode: 1
    - path: ''
      attribute: LeftFootQ.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.3
            inSlope: -0.6
            outSlope: 0.9
            tangentMode: 1
""";

        AnimationClip clip = ImportClip(yaml);
        var sceneNode = GetSceneNodeRoot(clip);
        var hipsTransform = GetTransformMember(sceneNode, "Hips");

        hipsTransform.Children.Any(x => x.MemberName == "Translation").ShouldBeFalse();
        hipsTransform.Children.Any(x => x.MemberName == "Rotation").ShouldBeFalse();

        var scaleX = GetChild(hipsTransform, "ScaleX", EAnimationMemberType.Property);
        var scaleAnim = scaleX.Animation.ShouldBeOfType<PropAnimFloat>();
        scaleAnim.Keyframes.Count.ShouldBe(2);
        scaleAnim.Keyframes[0].InValue.ShouldBe(1.0f);
        scaleAnim.Keyframes[0].OutValue.ShouldBe(1.0f);
        scaleAnim.Keyframes[0].InTangent.ShouldBe(-2.0f);
        scaleAnim.Keyframes[0].OutTangent.ShouldBe(3.0f);
        scaleAnim.Keyframes[1].InTangent.ShouldBe(4.0f);
        scaleAnim.Keyframes[1].OutTangent.ShouldBe(-5.0f);

        var translationZ = GetChild(hipsTransform, "TranslationZ", EAnimationMemberType.Property);
        var translationAnim = translationZ.Animation.ShouldBeOfType<PropAnimFloat>();
        translationAnim.Keyframes.Count.ShouldBe(1);
        translationAnim.Keyframes[0].InValue.ShouldBe(-0.5f);
        translationAnim.Keyframes[0].OutValue.ShouldBe(-0.5f);
        translationAnim.Keyframes[0].InTangent.ShouldBe(-1.25f);
        translationAnim.Keyframes[0].OutTangent.ShouldBe(2.5f);

        var quaternionX = GetChild(hipsTransform, "QuaternionX", EAnimationMemberType.Property);
        var quaternionAnim = quaternionX.Animation.ShouldBeOfType<PropAnimFloat>();
        quaternionAnim.Keyframes.Count.ShouldBe(1);
        quaternionAnim.Keyframes[0].InValue.ShouldBe(-0.25f);
        quaternionAnim.Keyframes[0].InTangent.ShouldBe(0.75f);
        quaternionAnim.Keyframes[0].OutTangent.ShouldBe(-1.5f);

        var humanoid = GetMethod(
            sceneNode,
            "GetComponentInHierarchy",
            animatedArgIndex: -1,
            methodArgs: ["HumanoidComponent"]);

        var rootPositionX = GetMethod(humanoid, "SetRootPositionX", animatedArgIndex: 0, methodArgs: [0.0f]);
        var rootPositionXAnim = rootPositionX.Animation.ShouldBeOfType<PropAnimFloat>();
        rootPositionXAnim.Keyframes[0].InValue.ShouldBe(-0.4f);
        rootPositionXAnim.Keyframes[0].InTangent.ShouldBe(0.25f);
        rootPositionXAnim.Keyframes[0].OutTangent.ShouldBe(-0.5f);

        var rootPositionZ = GetMethod(humanoid, "SetRootPositionZ", animatedArgIndex: 0, methodArgs: [0.0f]);
        var rootPositionAnim = rootPositionZ.Animation.ShouldBeOfType<PropAnimFloat>();
        rootPositionAnim.Keyframes[0].InValue.ShouldBe(0.75f);
        rootPositionAnim.Keyframes[0].InTangent.ShouldBe(-1.0f);
        rootPositionAnim.Keyframes[0].OutTangent.ShouldBe(2.0f);

        var rootRotationX = GetMethod(humanoid, "SetRootRotationX", animatedArgIndex: 0, methodArgs: [0.0f]);
        var rootRotationAnim = rootRotationX.Animation.ShouldBeOfType<PropAnimFloat>();
        rootRotationAnim.Keyframes[0].InValue.ShouldBe(0.2f);
        rootRotationAnim.Keyframes[0].InTangent.ShouldBe(0.4f);
        rootRotationAnim.Keyframes[0].OutTangent.ShouldBe(-0.8f);

        var rootRotationY = GetMethod(humanoid, "SetRootRotationY", animatedArgIndex: 0, methodArgs: [0.0f]);
        var rootRotationYAnim = rootRotationY.Animation.ShouldBeOfType<PropAnimFloat>();
        rootRotationYAnim.Keyframes[0].InValue.ShouldBe(-0.15f);
        rootRotationYAnim.Keyframes[0].InTangent.ShouldBe(0.3f);
        rootRotationYAnim.Keyframes[0].OutTangent.ShouldBe(-0.45f);

        var ikSolver = GetMethod(
            sceneNode,
            "GetComponentInHierarchy",
            animatedArgIndex: -1,
            methodArgs: ["HumanoidIKSolverComponent"]);

        var leftFootPositionX = GetMethod(
            ikSolver,
            "SetAnimatedIKPositionX",
            animatedArgIndex: 1,
            methodArgs: [ELimbEndEffector.LeftFoot, 0.0f]);
        var leftFootPositionXAnim = leftFootPositionX.Animation.ShouldBeOfType<PropAnimFloat>();
        leftFootPositionXAnim.Keyframes[0].InValue.ShouldBe(-0.6f);
        leftFootPositionXAnim.Keyframes[0].InTangent.ShouldBe(0.7f);
        leftFootPositionXAnim.Keyframes[0].OutTangent.ShouldBe(-0.8f);

        var leftFootPositionZ = GetMethod(
            ikSolver,
            "SetAnimatedIKPositionZ",
            animatedArgIndex: 1,
            methodArgs: [ELimbEndEffector.LeftFoot, 0.0f]);
        var leftFootPositionAnim = leftFootPositionZ.Animation.ShouldBeOfType<PropAnimFloat>();
        leftFootPositionAnim.Keyframes[0].InValue.ShouldBe(0.9f);
        leftFootPositionAnim.Keyframes[0].InTangent.ShouldBe(-1.1f);
        leftFootPositionAnim.Keyframes[0].OutTangent.ShouldBe(1.2f);

        var leftFootRotationX = GetMethod(
            ikSolver,
            "SetAnimatedIKRotationX",
            animatedArgIndex: 1,
            methodArgs: [ELimbEndEffector.LeftFoot, 0.0f]);
        var leftFootRotationAnim = leftFootRotationX.Animation.ShouldBeOfType<PropAnimFloat>();
        leftFootRotationAnim.Keyframes[0].InValue.ShouldBe(0.3f);
        leftFootRotationAnim.Keyframes[0].InTangent.ShouldBe(-0.6f);
        leftFootRotationAnim.Keyframes[0].OutTangent.ShouldBe(0.9f);

        var leftFootRotationY = GetMethod(
            ikSolver,
            "SetAnimatedIKRotationY",
            animatedArgIndex: 1,
            methodArgs: [ELimbEndEffector.LeftFoot, 0.0f]);
        var leftFootRotationYAnim = leftFootRotationY.Animation.ShouldBeOfType<PropAnimFloat>();
        leftFootRotationYAnim.Keyframes[0].InValue.ShouldBe(-0.35f);
        leftFootRotationYAnim.Keyframes[0].InTangent.ShouldBe(0.5f);
        leftFootRotationYAnim.Keyframes[0].OutTangent.ShouldBe(-0.7f);
    }

    [Test]
    public void Import_VectorCurves_RoutePerComponentWithoutResampling()
    {
        const string yaml = """
AnimationClip:
  m_Name: VectorClip
  m_SampleRate: 30
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_PositionCurves:
    - path: Armature/Hips
      attribute: m_LocalPosition
      classID: 4
      curve:
        x:
          m_Curve:
            - time: 0
              value: 1
              inSlope: 0
              outSlope: 0.5
              tangentMode: 1
        z:
          m_Curve:
            - time: 0
              value: 0.75
              inSlope: -1
              outSlope: 2
              tangentMode: 1
            - time: 0.5
              value: 1.25
              inSlope: -3
              outSlope: 4
              tangentMode: 1
  m_RotationCurves:
    - path: Armature/Hips
      attribute: m_LocalRotation
      classID: 4
      curve:
        x:
          m_Curve:
            - time: 0
              value: 0.2
              inSlope: 0.4
              outSlope: -0.8
              tangentMode: 1
            - time: 0.5
              value: 0.6
              inSlope: -1.2
              outSlope: 1.6
              tangentMode: 1
        w:
          m_Curve:
            - time: 0
              value: 1
              inSlope: 0
              outSlope: 0
              tangentMode: 1
""";

        AnimationClip clip = ImportClip(yaml);
        var hipsTransform = GetTransformMember(GetSceneNodeRoot(clip), "Armature/Hips");

        var translationZ = GetChild(hipsTransform, "TranslationZ", EAnimationMemberType.Property);
        var translationAnim = translationZ.Animation.ShouldBeOfType<PropAnimFloat>();
        translationAnim.Keyframes.Count.ShouldBe(2);
        translationAnim.Keyframes[0].InValue.ShouldBe(-0.75f);
        translationAnim.Keyframes[0].InTangent.ShouldBe(1.0f);
        translationAnim.Keyframes[0].OutTangent.ShouldBe(-2.0f);
        translationAnim.Keyframes[1].InValue.ShouldBe(-1.25f);
        translationAnim.Keyframes[1].InTangent.ShouldBe(3.0f);
        translationAnim.Keyframes[1].OutTangent.ShouldBe(-4.0f);

        var quaternionX = GetChild(hipsTransform, "QuaternionX", EAnimationMemberType.Property);
        var quaternionAnim = quaternionX.Animation.ShouldBeOfType<PropAnimFloat>();
        quaternionAnim.Keyframes.Count.ShouldBe(2);
        quaternionAnim.Keyframes[0].InValue.ShouldBe(-0.2f);
        quaternionAnim.Keyframes[0].InTangent.ShouldBe(-0.4f);
        quaternionAnim.Keyframes[0].OutTangent.ShouldBe(0.8f);
        quaternionAnim.Keyframes[1].InValue.ShouldBe(-0.6f);
        quaternionAnim.Keyframes[1].InTangent.ShouldBe(1.2f);
        quaternionAnim.Keyframes[1].OutTangent.ShouldBe(-1.6f);

        var quaternionW = GetChild(hipsTransform, "QuaternionW", EAnimationMemberType.Property);
        quaternionW.Animation.ShouldBeOfType<PropAnimFloat>().Keyframes.Count.ShouldBe(1);

        hipsTransform.Children.Any(x => x.MemberName == "Translation").ShouldBeFalse();
        hipsTransform.Children.Any(x => x.MemberName == "Rotation").ShouldBeFalse();
    }

    [Test]
    public void Import_ScalarCurves_PreserveUnityTangentModeMetadata()
    {
        int brokenLinearConstant = CombineTangentMode(TangentMode.Linear, TangentMode.Constant, broken: true);
        int autoClampedAuto = CombineTangentMode(TangentMode.Auto, TangentMode.ClampedAuto, broken: false);

        string yaml = $$"""
AnimationClip:
  m_Name: TangentModes
  m_SampleRate: 60
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_FloatCurves:
    - path: Hips
      attribute: m_LocalScale.x
      classID: 4
      curve:
        m_Curve:
          - time: 0
            value: 1
            inSlope: -2
            outSlope: 3
            tangentMode: {{brokenLinearConstant}}
          - time: 1
            value: 2
            inSlope: 4
            outSlope: -5
            tangentMode: {{autoClampedAuto}}
""";

        AnimationClip clip = ImportClip(yaml);
        var hipsTransform = GetTransformMember(GetSceneNodeRoot(clip), "Hips");
        var scaleAnim = GetChild(hipsTransform, "ScaleX", EAnimationMemberType.Property).Animation.ShouldBeOfType<PropAnimFloat>();

        FloatKeyframe first = scaleAnim.Keyframes[0];
        first.UnityCombinedTangentMode.ShouldBe(brokenLinearConstant);
        first.UnityTangentsBroken.ShouldBeTrue();
        first.UnityLeftTangentMode.ShouldBe(TangentMode.Linear);
        first.UnityRightTangentMode.ShouldBe(TangentMode.Constant);
        first.InterpolationTypeIn.ShouldBe(EVectorInterpType.Linear);
        first.InterpolationTypeOut.ShouldBe(EVectorInterpType.Step);

        var roundTripped = new FloatKeyframe();
        roundTripped.ReadFromString(first.WriteToString());
        roundTripped.UnityCombinedTangentMode.ShouldBe(brokenLinearConstant);
        roundTripped.UnityTangentsBroken.ShouldBeTrue();
        roundTripped.UnityLeftTangentMode.ShouldBe(TangentMode.Linear);
        roundTripped.UnityRightTangentMode.ShouldBe(TangentMode.Constant);

        FloatKeyframe second = scaleAnim.Keyframes[1];
        second.UnityCombinedTangentMode.ShouldBe(autoClampedAuto);
        second.UnityTangentsBroken.ShouldBeFalse();
        second.UnityLeftTangentMode.ShouldBe(TangentMode.Auto);
        second.UnityRightTangentMode.ShouldBe(TangentMode.ClampedAuto);
        second.InterpolationTypeIn.ShouldBe(EVectorInterpType.Smooth);
        second.InterpolationTypeOut.ShouldBe(EVectorInterpType.Smooth);
    }

    [Test]
    public void HumanoidRootMotion_UsesSameAnimatedMotionScaleAsIKGoals()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(new Vector3(0.0f, 1.0f, 0.0f)));
        var leftFoot = new SceneNode(hips, "LeftFoot", new Transform(new Vector3(0.0f, -2.0f, 0.0f)));
        var rightFoot = new SceneNode(hips, "RightFoot", new Transform(new Vector3(0.0f, -2.0f, 0.0f)));

        root.Transform.SaveBindState();
        hips.Transform.SaveBindState();
        leftFoot.Transform.SaveBindState();
        rightFoot.Transform.SaveBindState();

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Hips.Node = hips;
        humanoid.Left.Foot.Node = leftFoot;
        humanoid.Right.Foot.Node = rightFoot;
        var hipsTransform = hips.GetTransformAs<Transform>(true)!;

        humanoid.EstimateAnimatedMotionScale().ShouldBe(2.0f, 0.0001f);

        var solver = root.AddComponent<HumanoidIKSolverComponent>()!;
        humanoid.Settings.IKGoalPolicy = EHumanoidIKGoalPolicy.AlwaysApply;

        solver.SetAnimatedIKPosition(ELimbEndEffector.LeftFoot, new Vector3(0.0f, 0.5f, 1.0f));
        var ikTarget = solver.GetGoalIK(ELimbEndEffector.LeftFoot)?.TargetIKTransform;
        ikTarget.ShouldNotBeNull();
        ShouldBeApproximately(ikTarget!.WorldTranslation, new Vector3(0.0f, 2.0f, 2.0f));

        humanoid.SetRootPosition(new Vector3(0.0f, 1.0f, 0.0f));
        ShouldBeApproximately(hipsTransform.Translation, new Vector3(0.0f, 1.0f, 0.0f));

        humanoid.SetRootPosition(new Vector3(0.0f, 1.5f, 1.0f));
        ShouldBeApproximately(hipsTransform.Translation, new Vector3(0.0f, 2.0f, 2.0f));
    }

    [Test]
    public void HumanoidRootMotion_ResetBaseline_ClearsPositionAndRotationState()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(new Vector3(0.0f, 1.0f, 0.0f)));
        var leftFoot = new SceneNode(hips, "LeftFoot", new Transform(new Vector3(0.0f, -2.0f, 0.0f)));
        var rightFoot = new SceneNode(hips, "RightFoot", new Transform(new Vector3(0.0f, -2.0f, 0.0f)));

        root.Transform.SaveBindState();
        hips.Transform.SaveBindState();
        leftFoot.Transform.SaveBindState();
        rightFoot.Transform.SaveBindState();

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Hips.Node = hips;
        humanoid.Left.Foot.Node = leftFoot;
        humanoid.Right.Foot.Node = rightFoot;
        var hipsTransform = hips.GetTransformAs<Transform>(true)!;

        humanoid.SetRootPosition(new Vector3(1.0f, 2.0f, 3.0f));
        humanoid.SetRootPosition(new Vector3(1.0f, 2.5f, 4.0f));
        ShouldBeApproximately(hipsTransform.Translation, new Vector3(0.0f, 2.0f, 2.0f));

        Quaternion ninety = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        Quaternion oneEighty = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

        humanoid.SetRootRotation(ninety);
        ShouldBeApproximately(hipsTransform.Rotation, Quaternion.Identity);

        humanoid.SetRootRotation(oneEighty);
        ShouldBeApproximately(hipsTransform.Rotation, ninety);

        humanoid.ResetRootMotionBaseline();

        humanoid.SetRootPosition(new Vector3(10.0f, 20.0f, 30.0f));
        ShouldBeApproximately(hipsTransform.Translation, new Vector3(0.0f, 1.0f, 0.0f));

        humanoid.SetRootRotation(oneEighty);
        ShouldBeApproximately(hipsTransform.Rotation, Quaternion.Identity);
    }

    private static AnimationClip ImportClip(string yaml)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "UnityAnimImporterTests",
            $"{Guid.NewGuid():N}.anim");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return AnimYamlImporter.Import(path);
    }

    private static AnimationMember GetSceneNodeRoot(AnimationClip clip)
    {
        AnimationMember? root = clip.RootMember;
        root.ShouldNotBeNull();
        return GetChild(root!, "SceneNode", EAnimationMemberType.Property);
    }

    private static int CombineTangentMode(TangentMode left, TangentMode right, bool broken)
        => (broken ? 1 : 0) | ((int)left << 1) | ((int)right << 5);

    private static AnimationMember GetTransformMember(AnimationMember sceneNodeRoot, string nodePath)
    {
        AnimationMember current = sceneNodeRoot;
        foreach (string segment in nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            current = GetMethod(
                current,
                "FindDescendantByName",
                animatedArgIndex: -1,
                methodArgs: [segment, StringComparison.InvariantCultureIgnoreCase]);
        }

        return GetChild(current, "Transform", EAnimationMemberType.Property);
    }

    private static AnimationMember GetChild(AnimationMember parent, string memberName, EAnimationMemberType memberType)
    {
        var child = parent.Children.SingleOrDefault(x => x.MemberName == memberName && x.MemberType == memberType);
        child.ShouldNotBeNull();
        return child!;
    }

    private static AnimationMember GetMethod(AnimationMember parent, string memberName, int animatedArgIndex, object?[] methodArgs)
    {
        var child = parent.Children.SingleOrDefault(x =>
            x.MemberName == memberName &&
            x.MemberType == EAnimationMemberType.Method &&
            x.AnimatedMethodArgumentIndex == animatedArgIndex &&
            x.MethodArguments.Length == methodArgs.Length &&
            x.MethodArguments.SequenceEqual(methodArgs));
        child.ShouldNotBeNull();
        return child!;
    }

    private static void ShouldBeApproximately(Vector3 actual, Vector3 expected, float tolerance = 0.0001f)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }

    private static void ShouldBeApproximately(Quaternion actual, Quaternion expected, float tolerance = 0.0001f)
    {
        if (Quaternion.Dot(actual, expected) < 0.0f)
            actual = Quaternion.Negate(actual);

        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
        actual.W.ShouldBe(expected.W, tolerance);
    }
}
