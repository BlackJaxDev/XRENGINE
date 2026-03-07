using System;
using System.IO;
using System.Linq;
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

    private static void InvokePrivate(MethodInfo method, object target)
        => method.Invoke(target, null);

    private static string WriteTempAnimYaml(string contents)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{Guid.NewGuid():N}.anim");
        File.WriteAllText(path, contents.ReplaceLineEndings(Environment.NewLine));
        return path;
    }
}
