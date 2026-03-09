using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Animation.IK;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationClipComponentTests
{
    private static readonly MethodInfo ApplyRuntimeClipRemapsMethod =
        typeof(AnimationClipComponent).GetMethod("ApplyRuntimeClipRemaps", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimationClipComponent.ApplyRuntimeClipRemaps.");
    private static readonly MethodInfo StartMethod =
        typeof(AnimationClipComponent).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimationClipComponent.Start.");
    private static readonly MethodInfo StopMethod =
        typeof(AnimationClipComponent).GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimationClipComponent.Stop.");
    private static readonly MethodInfo TickAnimationMethod =
        typeof(AnimationClipComponent).GetMethod("TickAnimation", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimationClipComponent.TickAnimation.");

    [TestCase("SetAnimatedIKPositionX", 1.25f, 1.25f)]
    [TestCase("SetAnimatedIKPositionY", 1.25f, 1.25f)]
    [TestCase("SetAnimatedIKPositionZ", 1.25f, -1.25f)]
    public void FlipIKPositionZ_AppliesToScalarUnityIkChannels(string methodName, float input, float expected)
    {
        var component = new AnimationClipComponent
        {
            FlipIKPositionZ = true
        };
        var member = CreateMethodMember(methodName, ELimbEndEffector.LeftFoot);

        object? remapped = InvokeRuntimeRemap(component, member, input);

        remapped.ShouldBeOfType<float>().ShouldBe(expected);
        member.MethodArguments[0].ShouldBe(ELimbEndEffector.LeftFoot);
    }

    [Test]
    public void FlipIKPositionLeftRight_SwapsScalarUnityIkGoal()
    {
        var component = new AnimationClipComponent
        {
            FlipIKPositionLeftRight = true
        };
        var member = CreateMethodMember("SetAnimatedIKPositionY", ELimbEndEffector.LeftFoot);

        object? remapped = InvokeRuntimeRemap(component, member, 0.5f);

        remapped.ShouldBeOfType<float>().ShouldBe(0.5f);
        member.MethodArguments[0].ShouldBe(ELimbEndEffector.RightFoot);
    }

    [TestCase("SetAnimatedIKRotationX", 0.25f, -0.25f)]
    [TestCase("SetAnimatedIKRotationY", 0.25f, -0.25f)]
    [TestCase("SetAnimatedIKRotationZ", 0.25f, 0.25f)]
    [TestCase("SetAnimatedIKRotationW", 0.25f, 0.25f)]
    public void FlipIKRotationZ_AppliesToScalarUnityIkChannels(string methodName, float input, float expected)
    {
        var component = new AnimationClipComponent
        {
            FlipIKRotationZ = true
        };
        var member = CreateMethodMember(methodName, ELimbEndEffector.LeftHand);

        object? remapped = InvokeRuntimeRemap(component, member, input);

        remapped.ShouldBeOfType<float>().ShouldBe(expected);
        member.MethodArguments[0].ShouldBe(ELimbEndEffector.LeftHand);
    }

    [Test]
    public void FlipIKRotationLeftRight_SwapsScalarUnityIkGoal()
    {
        var component = new AnimationClipComponent
        {
            FlipIKRotationLeftRight = true
        };
        var member = CreateMethodMember("SetAnimatedIKRotationW", ELimbEndEffector.RightHand);

        object? remapped = InvokeRuntimeRemap(component, member, 0.75f);

        remapped.ShouldBeOfType<float>().ShouldBe(0.75f);
        member.MethodArguments[0].ShouldBe(ELimbEndEffector.LeftHand);
    }

    [Test]
    public void EvaluateAtTime_UsesExactNonWrappingTimeForLoopedClip()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());

        var clip = new AnimationClip
        {
            Name = "LoopedSample",
            LengthInSeconds = 1.0f,
            Looped = true,
            SampleRate = 60,
            RootMember = CreateTranslationClipRoot(CreateLinearFloatAnimation(0.0f, 10.0f)),
        };

        var component = root.AddComponent<AnimationClipComponent>()!;
        component.Animation = clip;

        component.EvaluateAtTime(1.0f);

        hips.GetTransformAs<Transform>(true)!.Translation.X.ShouldBe(10.0f, 0.0001f);
        component.PlaybackTime.ShouldBe(1.0f, 0.0001f);
    }

    [Test]
    public void Start_WithNegativeSpeed_BeginsFromClipEnd()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());

        var clip = new AnimationClip
        {
            Name = "ReverseStart",
            LengthInSeconds = 1.0f,
            Looped = true,
            SampleRate = 60,
            RootMember = CreateTranslationClipRoot(CreateLinearFloatAnimation(0.0f, 10.0f)),
        };

        var component = root.AddComponent<AnimationClipComponent>()!;
        component.Animation = clip;
        component.Speed = -1.0f;

        try
        {
            InvokePrivate(StartMethod, component);

            component.PlaybackTime.ShouldBe(1.0f, 0.0001f);
            hips.GetTransformAs<Transform>(true)!.Translation.X.ShouldBe(10.0f, 0.0001f);
        }
        finally
        {
            InvokePrivate(StopMethod, component);
        }
    }

    [Test]
    public void TickAnimation_UsesCanonicalClipTimeInsteadOfChildAnimationSpeed()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());

        var floatAnimation = CreateLinearFloatAnimation(0.0f, 10.0f);
        floatAnimation.Speed = 2.0f;

        var clip = new AnimationClip
        {
            Name = "CanonicalTime",
            LengthInSeconds = 1.0f,
            Looped = true,
            SampleRate = 60,
            RootMember = CreateTranslationClipRoot(floatAnimation),
        };

        var component = root.AddComponent<AnimationClipComponent>()!;
        component.Animation = clip;

        float previousDelta = Engine.Time.Timer.Update.Delta;
        try
        {
            Engine.Time.Timer.Update.Delta = 0.25f;

            InvokePrivate(StartMethod, component);
            InvokePrivate(TickAnimationMethod, component);

            component.PlaybackTime.ShouldBe(0.25f, 0.0001f);
            hips.GetTransformAs<Transform>(true)!.Translation.X.ShouldBe(2.5f, 0.0001f);
        }
        finally
        {
            Engine.Time.Timer.Update.Delta = previousDelta;
            InvokePrivate(StopMethod, component);
        }
    }

    [Test]
    public void EvaluateAtTime_NormalizesAnimatedQuaternionComponentsAfterApply()
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform());

        var clip = new AnimationClip
        {
            Name = "QuaternionNormalize",
            LengthInSeconds = 1.0f,
            Looped = false,
            SampleRate = 60,
            RootMember = CreateQuaternionClipRoot(
                CreateConstantFloatAnimation(1.0f),
                CreateConstantFloatAnimation(0.0f),
                CreateConstantFloatAnimation(0.0f),
                CreateConstantFloatAnimation(1.0f)),
        };

        var component = root.AddComponent<AnimationClipComponent>()!;
        component.Animation = clip;

        component.EvaluateAtTime(0.0f);

        hips.GetTransformAs<Transform>(true)!.Rotation.LengthSquared().ShouldBe(1.0f, 0.0001f);
    }

    [Test]
    public void ImportedUnityCurve_ShiftsStartTimeAndAppliesClampInfinity()
    {
        string path = WriteTempAnimYaml(
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!74 &7400000
            AnimationClip:
              m_Name: ShiftedClip
              m_SampleRate: 30
              m_FloatCurves:
              - curve:
                  serializedVersion: 2
                  m_Curve:
                  - serializedVersion: 2
                    time: 1
                    value: 0
                    inSlope: 0
                    outSlope: 0
                    tangentMode: 0
                  - serializedVersion: 2
                    time: 2
                    value: 10
                    inSlope: 0
                    outSlope: 0
                    tangentMode: 0
                  m_PreInfinity: 0
                  m_PostInfinity: 0
                  m_RotationOrder: 4
                attribute: m_LocalPosition.x
                path: Hips
                classID: 4
                script: {fileID: 0}
              m_AnimationClipSettings:
                serializedVersion: 2
                m_StartTime: 1
                m_StopTime: 2
                m_LoopTime: 0
            """);

        try
        {
            var clip = AnimYamlImporter.Import(path);
            var anim = clip.GetAllAnimations().Values.OfType<PropAnimFloat>().Single();

            anim.Keyframes[0].Second.ShouldBe(0.0f, 0.0001f);
            anim.Keyframes[1].Second.ShouldBe(1.0f, 0.0001f);
            anim.Keyframes.PreInfinityMode.ShouldBe(EKeyframeInfinityMode.Clamp);
            anim.Keyframes.PostInfinityMode.ShouldBe(EKeyframeInfinityMode.Clamp);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void ClampInfinity_DoesNotBlendBackToFirstKeyBeforeClipEnd()
    {
        var animation = new PropAnimFloat(1.0f, looped: false, useKeyframes: true);
        animation.Keyframes.PreInfinityMode = EKeyframeInfinityMode.Clamp;
        animation.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(0.25f, 10.0f, 0.0f, EVectorInterpType.Linear));

        animation.GetValue(0.75f).ShouldBe(10.0f, 0.0001f);
    }

    [Test]
    public void ClampInfinity_MakeLinearTangents_DoesNotWrapAcrossSeam()
    {
        var animation = new PropAnimFloat(1.0f, looped: false, useKeyframes: true);
        animation.Keyframes.PreInfinityMode = EKeyframeInfinityMode.Clamp;
        animation.Keyframes.PostInfinityMode = EKeyframeInfinityMode.Clamp;
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, 0.0f, 5.0f, EVectorInterpType.Linear),
            new FloatKeyframe(0.25f, 10.0f, -7.0f, EVectorInterpType.Linear));

        animation.Keyframes[1].MakeOutLinear();
        animation.Keyframes[0].MakeInLinear();

        animation.Keyframes[1].OutTangent.ShouldBe(-7.0f, 0.0001f);
        animation.Keyframes[0].InTangent.ShouldBe(5.0f, 0.0001f);
    }

    [Test]
    public void ImportedUnityHumanoidClip_EvaluateAtTime_UsesRawCurveValues()
    {
        string repoRoot = FindRepositoryRoot();
        string clipPath = Path.Combine(repoRoot, "Assets", "Walks", "Sexy Walk.anim");
        var clip = AnimYamlImporter.Import(clipPath);

        var root = new SceneNode("Root", new Transform());
        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;
        var humanoid = root.AddComponent<HumanoidComponent>()!;

        clipComponent.EvaluateAtTime(0.0f);

        humanoid.TryGetRawHumanoidValue(EHumanoidValue.SpineFrontBack, out float rawSpineFrontBack).ShouldBeTrue();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out float rawLeftArmDownUp).ShouldBeTrue();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftUpperLegFrontBack, out float rawLeftUpperLegFrontBack).ShouldBeTrue();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.RightUpperLegFrontBack, out float rawRightUpperLegFrontBack).ShouldBeTrue();

        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float spineFrontBack).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out float leftArmDownUp).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftUpperLegFrontBack, out float leftUpperLegFrontBack).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.RightUpperLegFrontBack, out float rightUpperLegFrontBack).ShouldBeTrue();

        rawSpineFrontBack.ShouldBe(0.090756066f, 0.000001f);
        rawLeftArmDownUp.ShouldBe(-0.687864f, 0.000001f);
        rawLeftUpperLegFrontBack.ShouldBe(0.8625245f, 0.000001f);
        rawRightUpperLegFrontBack.ShouldBe(-0.021309234f, 0.000001f);

        spineFrontBack.ShouldBe(0.090756066f, 0.000001f);
        leftArmDownUp.ShouldBe(-0.687864f, 0.000001f);
        leftUpperLegFrontBack.ShouldBe(0.8625245f, 0.000001f);
        rightUpperLegFrontBack.ShouldBe(-0.021309234f, 0.000001f);
    }

    [Test]
    public void ImportedUnityHumanoidClip_FlipMuscleZ_PreservesRawAndOnlyFlipsPitchYawFamilies()
    {
        string repoRoot = FindRepositoryRoot();
        string clipPath = Path.Combine(repoRoot, "Assets", "Walks", "Sexy Walk.anim");
        var clip = AnimYamlImporter.Import(clipPath);

        var root = new SceneNode("Root", new Transform());
        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;
        clipComponent.FlipMuscleZ = true;
        var humanoid = root.AddComponent<HumanoidComponent>()!;

        clipComponent.EvaluateAtTime(0.0f);

        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out float rawLeftArmDownUp).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out float convertedLeftArmDownUp).ShouldBeTrue();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.SpineLeftRight, out float rawSpineLeftRight).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineLeftRight, out float convertedSpineLeftRight).ShouldBeTrue();

        rawLeftArmDownUp.ShouldBe(-0.687864f, 0.000001f);
        convertedLeftArmDownUp.ShouldBe(0.687864f, 0.000001f);
        convertedSpineLeftRight.ShouldBe(rawSpineLeftRight, 0.000001f);
    }

    [Test]
    public void Stop_HumanoidClip_RestoresBindPoseAndClearsHumanoidMuscles()
    {
        string repoRoot = FindRepositoryRoot();
        string clipPath = Path.Combine(repoRoot, "Assets", "Walks", "Sexy Walk.anim");
        var clip = AnimYamlImporter.Import(clipPath);

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

        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;
        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();

        clipComponent.EvaluateAtTime(0.0f);

        var leftArmTransform = leftArm.GetTransformAs<Transform>(true)!;
        Quaternion bindRotation = Quaternion.Normalize(leftArmTransform.BindState.Rotation);
        Quaternion posedRotation = Quaternion.Normalize(leftArmTransform.Rotation);
        Quaternion.Dot(posedRotation, bindRotation).ShouldBeLessThan(0.999f);
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeTrue();

        InvokePrivate(StopMethod, clipComponent);

        humanoid.TryGetMuscleValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeFalse();
        humanoid.TryGetRawHumanoidValue(EHumanoidValue.LeftArmDownUp, out _).ShouldBeFalse();
        AssertEquivalent(Quaternion.Normalize(leftArmTransform.Rotation), bindRotation);
    }

    private static AnimationMember CreateMethodMember(string methodName, ELimbEndEffector goal)
        => new AnimationMember(methodName, EAnimationMemberType.Method, animation: null)
        {
            MethodArguments = [goal, 0.0f],
            AnimatedMethodArgumentIndex = 1
        };

    private static AnimationMember CreateTranslationClipRoot(PropAnimFloat animation)
    {
        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var findHips = new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
        {
            MethodArguments = ["Hips", StringComparison.InvariantCultureIgnoreCase],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        var transform = new AnimationMember("Transform", EAnimationMemberType.Property);
        var translationX = new AnimationMember("TranslationX", EAnimationMemberType.Property, animation);

        root.Children.Add(sceneNode);
        sceneNode.Children.Add(findHips);
        findHips.Children.Add(transform);
        transform.Children.Add(translationX);
        return root;
    }

    private static AnimationMember CreateQuaternionClipRoot(PropAnimFloat x, PropAnimFloat y, PropAnimFloat z, PropAnimFloat w)
    {
        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var findHips = new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
        {
            MethodArguments = ["Hips", StringComparison.InvariantCultureIgnoreCase],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        var transform = new AnimationMember("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("QuaternionX", EAnimationMemberType.Property, x));
        transform.Children.Add(new AnimationMember("QuaternionY", EAnimationMemberType.Property, y));
        transform.Children.Add(new AnimationMember("QuaternionZ", EAnimationMemberType.Property, z));
        transform.Children.Add(new AnimationMember("QuaternionW", EAnimationMemberType.Property, w));

        root.Children.Add(sceneNode);
        sceneNode.Children.Add(findHips);
        findHips.Children.Add(transform);
        return root;
    }

    private static PropAnimFloat CreateLinearFloatAnimation(float start, float end)
    {
        var animation = new PropAnimFloat(1.0f, looped: true, useKeyframes: true);
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, start, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, end, 0.0f, EVectorInterpType.Linear));
        return animation;
    }

    private static PropAnimFloat CreateConstantFloatAnimation(float value)
    {
        var animation = new PropAnimFloat(1.0f, looped: false, useKeyframes: true);
        animation.Keyframes.Add(new FloatKeyframe(0.0f, value, 0.0f, EVectorInterpType.Step));
        return animation;
    }

    private static object? InvokeRuntimeRemap(AnimationClipComponent component, AnimationMember member, object? value)
    {
        object?[] args = [member, value];
        ApplyRuntimeClipRemapsMethod.Invoke(component, args);
        return args[1];
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

    private static string WriteTempAnimYaml(string contents)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.anim");
        File.WriteAllText(path, contents.ReplaceLineEndings(Environment.NewLine));
        return path;
    }

    private static string FindRepositoryRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);

        while (true)
        {
            if (File.Exists(Path.Combine(current, "XRENGINE.sln")))
                return current;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root containing XRENGINE.sln.");
    }
}
