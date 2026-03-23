using System;
using System.Linq;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationClipSerializationTests
{
    [Test]
    public void YamlSerializer_RoundTrips_AnimationClipTree()
    {
        AnimationClip original = CreateSampleClip();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(AnimationClip));
        yaml.ShouldContain("RootMember:");
        yaml.ShouldNotContain("Initialize:");
        yaml.ShouldNotContain("ParentClip:");

        AnimationClip? clone = AssetManager.Deserializer.Deserialize<AnimationClip>(yaml);
        clone.ShouldNotBeNull();

        AssertClipsEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_AnimationClipTree()
    {
        AnimationClip original = CreateSampleClip();

        byte[] payload = CookedBinarySerializer.Serialize(original);
        payload.Length.ShouldBeGreaterThan(0);

        AnimationClip? clone = CookedBinarySerializer.Deserialize(typeof(AnimationClip), payload) as AnimationClip;
        clone.ShouldNotBeNull();

        AssertClipsEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_AnimationClipTree()
    {
        AnimationClip original = CreateSampleClip();

        AnimationClipSerializedModel model = AnimationClipSerialization.CreateModel(original);
        byte[] payload = MemoryPackSerializer.Serialize(model);
        payload.Length.ShouldBeGreaterThan(0);

        AnimationClipSerializedModel? cloneModel = MemoryPackSerializer.Deserialize<AnimationClipSerializedModel>(payload);
        cloneModel.ShouldNotBeNull();

        AnimationClip clone = new();
        AnimationClipSerialization.ApplyModel(clone, cloneModel);

        AssertClipsEquivalent(original, clone);
    }

    private static AnimationClip CreateSampleClip()
    {
        PropAnimFloat animation = new(24, 24.0f, looped: true, useKeyframes: true)
        {
            Name = "HipRotationX"
        };
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, -0.5f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, 0.75f, 0.0f, EVectorInterpType.Linear));

        AnimationMember root = new("Root", EAnimationMemberType.Group);
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        AnimationMember findHips = new("FindDescendantByName", EAnimationMemberType.Method)
        {
            MethodArguments = ["Hips", StringComparison.InvariantCultureIgnoreCase],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("QuaternionX", EAnimationMemberType.Property, animation));
        root.Children.Add(sceneNode);
        sceneNode.Children.Add(findHips);
        findHips.Children.Add(transform);

        AnimationClip clip = new(root)
        {
            Name = "Walk",
            OriginalPath = "Assets\\Walks\\Walk.anim",
            OriginalLastWriteTimeUtc = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc),
            TraversalMethod = EAnimTreeTraversalMethod.BreadthFirst,
            Looped = true,
            ClipKind = EAnimationClipKind.UnityHumanoidMuscle,
            HasMuscleChannels = true,
            HasRootMotion = true,
            HasIKGoals = false,
            SampleRate = 24,
            LengthInSeconds = 1.0f
        };

        return clip;
    }

    private static void AssertClipsEquivalent(AnimationClip expected, AnimationClip actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.OriginalPath.ShouldBe(expected.OriginalPath);
        actual.OriginalLastWriteTimeUtc.ShouldBe(expected.OriginalLastWriteTimeUtc);
        actual.TraversalMethod.ShouldBe(expected.TraversalMethod);
        actual.LengthInSeconds.ShouldBe(expected.LengthInSeconds);
        actual.Looped.ShouldBe(expected.Looped);
        actual.ClipKind.ShouldBe(expected.ClipKind);
        actual.HasMuscleChannels.ShouldBe(expected.HasMuscleChannels);
        actual.HasRootMotion.ShouldBe(expected.HasRootMotion);
        actual.HasIKGoals.ShouldBe(expected.HasIKGoals);
        actual.SampleRate.ShouldBe(expected.SampleRate);
        actual.RootMember.ShouldNotBeNull();
        actual.RootMember!.ParentClip.ShouldBe(actual);

        AssertMembersEquivalent(expected.RootMember, actual.RootMember);

        actual.GetAllAnimations().Count.ShouldBe(expected.GetAllAnimations().Count);
    }

    private static void AssertMembersEquivalent(AnimationMember? expected, AnimationMember? actual)
    {
        if (expected is null || actual is null)
        {
            actual.ShouldBe(expected);
            return;
        }

        actual.MemberName.ShouldBe(expected.MemberName);
        actual.MemberType.ShouldBe(expected.MemberType);
        actual.CacheReturnValue.ShouldBe(expected.CacheReturnValue);
        actual.AnimatedMethodArgumentIndex.ShouldBe(expected.AnimatedMethodArgumentIndex);
        actual.MethodArguments.ShouldBe(expected.MethodArguments);
        actual.Children.Count.ShouldBe(expected.Children.Count);

        if (expected.Animation is null)
        {
            actual.Animation.ShouldBeNull();
        }
        else
        {
            actual.Animation.ShouldNotBeNull();
            actual.Animation!.GetType().ShouldBe(expected.Animation.GetType());
            actual.Animation.Name.ShouldBe(expected.Animation.Name);
            actual.Animation.AuthoredCadence.ShouldBe(expected.Animation.AuthoredCadence);
            actual.Animation.Looped.ShouldBe(expected.Animation.Looped);

            if (expected.Animation is PropAnimFloat expectedFloat)
            {
                PropAnimFloat actualFloat = actual.Animation.ShouldBeOfType<PropAnimFloat>();
                actualFloat.Keyframes.Count.ShouldBe(expectedFloat.Keyframes.Count);

                for (int i = 0; i < expectedFloat.Keyframes.Count; i++)
                {
                    FloatKeyframe expectedFrame = expectedFloat.Keyframes[i];
                    FloatKeyframe actualFrame = actualFloat.Keyframes[i];
                    actualFrame.Second.ShouldBe(expectedFrame.Second);
                    actualFrame.InValue.ShouldBe(expectedFrame.InValue);
                    actualFrame.OutValue.ShouldBe(expectedFrame.OutValue);
                    actualFrame.InterpolationTypeIn.ShouldBe(expectedFrame.InterpolationTypeIn);
                    actualFrame.InterpolationTypeOut.ShouldBe(expectedFrame.InterpolationTypeOut);
                }
            }
        }

        for (int i = 0; i < expected.Children.Count; i++)
            AssertMembersEquivalent(expected.Children[i], actual.Children[i]);
    }
}