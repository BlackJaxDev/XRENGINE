using System;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
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

    private static PropAnimFloat CreateLinearFloatAnimation(float start, float end)
    {
        var animation = new PropAnimFloat(1.0f, looped: true, useKeyframes: true);
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, start, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, end, 0.0f, EVectorInterpType.Linear));
        return animation;
    }

    private static object? InvokeRuntimeRemap(AnimationClipComponent component, AnimationMember member, object? value)
    {
        object?[] args = [member, value];
        ApplyRuntimeClipRemapsMethod.Invoke(component, args);
        return args[1];
    }
}
