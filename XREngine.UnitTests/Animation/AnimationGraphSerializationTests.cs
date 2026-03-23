using System;
using System.Linq;
using System.Numerics;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class AnimationGraphSerializationTests
{
    [Test]
    public void YamlSerializer_RoundTrips_BlendTree1D()
    {
        BlendTree1D original = CreateSampleBlendTree();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(BlendTree1D));
        yaml.ShouldContain("Children:");
        yaml.ShouldContain("Threshold:");

        BlendTree1D? clone = AssetManager.Deserializer.Deserialize<BlendTree1D>(yaml);
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_BlendTree1D()
    {
        BlendTree1D original = CreateSampleBlendTree();

        byte[] payload = CookedBinarySerializer.Serialize(original);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTree1D? clone = CookedBinarySerializer.Deserialize(typeof(BlendTree1D), payload) as BlendTree1D;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_BlendTree1D()
    {
        BlendTree1D original = CreateSampleBlendTree();

        BlendTree1DSerializedModel model = (BlendTree1DSerializedModel)BlendTreeSerialization.CreateModel(original);
        byte[] payload = MemoryPackSerializer.Serialize(model);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTree1DSerializedModel? cloneModel = MemoryPackSerializer.Deserialize<BlendTree1DSerializedModel>(payload);
        cloneModel.ShouldNotBeNull();

        BlendTree1D? clone = BlendTreeSerialization.CreateRuntimeBlendTree(typeof(BlendTree1D), cloneModel) as BlendTree1D;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void YamlSerializer_RoundTrips_BlendTree2D()
    {
        BlendTree2D original = CreateSampleBlendTree2D();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(BlendTree2D));
        yaml.ShouldContain("Children:");
        yaml.ShouldContain("PositionX:");
        yaml.ShouldContain("PositionY:");

        BlendTree2D? clone = AssetManager.Deserializer.Deserialize<BlendTree2D>(yaml);
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_BlendTree2D()
    {
        BlendTree2D original = CreateSampleBlendTree2D();

        byte[] payload = CookedBinarySerializer.Serialize(original);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTree2D? clone = CookedBinarySerializer.Deserialize(typeof(BlendTree2D), payload) as BlendTree2D;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_BlendTree2D()
    {
        BlendTree2D original = CreateSampleBlendTree2D();

        BlendTree2DSerializedModel model = (BlendTree2DSerializedModel)BlendTreeSerialization.CreateModel(original);
        byte[] payload = MemoryPackSerializer.Serialize(model);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTree2DSerializedModel? cloneModel = MemoryPackSerializer.Deserialize<BlendTree2DSerializedModel>(payload);
        cloneModel.ShouldNotBeNull();

        BlendTree2D? clone = BlendTreeSerialization.CreateRuntimeBlendTree(typeof(BlendTree2D), cloneModel) as BlendTree2D;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void YamlSerializer_RoundTrips_BlendTreeDirect()
    {
        BlendTreeDirect original = CreateSampleBlendTreeDirect();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(BlendTreeDirect));
        yaml.ShouldContain("Children:");
        yaml.ShouldContain("WeightParameterName:");

        BlendTreeDirect? clone = AssetManager.Deserializer.Deserialize<BlendTreeDirect>(yaml);
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_BlendTreeDirect()
    {
        BlendTreeDirect original = CreateSampleBlendTreeDirect();

        byte[] payload = CookedBinarySerializer.Serialize(original);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTreeDirect? clone = CookedBinarySerializer.Deserialize(typeof(BlendTreeDirect), payload) as BlendTreeDirect;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_BlendTreeDirect()
    {
        BlendTreeDirect original = CreateSampleBlendTreeDirect();

        BlendTreeDirectSerializedModel model = (BlendTreeDirectSerializedModel)BlendTreeSerialization.CreateModel(original);
        byte[] payload = MemoryPackSerializer.Serialize(model);
        payload.Length.ShouldBeGreaterThan(0);

        BlendTreeDirectSerializedModel? cloneModel = MemoryPackSerializer.Deserialize<BlendTreeDirectSerializedModel>(payload);
        cloneModel.ShouldNotBeNull();

        BlendTreeDirect? clone = BlendTreeSerialization.CreateRuntimeBlendTree(typeof(BlendTreeDirect), cloneModel) as BlendTreeDirect;
        clone.ShouldNotBeNull();

        AssertBlendTreesEquivalent(original, clone!);
    }

    [Test]
    public void YamlSerializer_RoundTrips_AnimStateMachineGraph()
    {
        AnimStateMachine original = CreateSampleStateMachine();

        string yaml = AssetManager.Serializer.Serialize(original, typeof(AnimStateMachine));
        yaml.ShouldContain("AnyStateTransitions:");
        yaml.ShouldContain("Components:");

        AnimStateMachine? clone = AssetManager.Deserializer.Deserialize<AnimStateMachine>(yaml);
        clone.ShouldNotBeNull();

        AssertStateMachinesEquivalent(original, clone!);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_AnimStateMachineGraph()
    {
        AnimStateMachine original = CreateSampleStateMachine();

        byte[] payload = CookedBinarySerializer.Serialize(original);
        payload.Length.ShouldBeGreaterThan(0);

        AnimStateMachine? clone = CookedBinarySerializer.Deserialize(typeof(AnimStateMachine), payload) as AnimStateMachine;
        clone.ShouldNotBeNull();

        AssertStateMachinesEquivalent(original, clone!);
    }

    [Test]
    public void MemoryPackSerializer_RoundTrips_AnimStateMachineGraph()
    {
        AnimStateMachine original = CreateSampleStateMachine();

        AnimStateMachineSerializedModel model = AnimStateMachineSerialization.CreateModel(original);
        byte[] payload = MemoryPackSerializer.Serialize(model);
        payload.Length.ShouldBeGreaterThan(0);

        AnimStateMachineSerializedModel? cloneModel = MemoryPackSerializer.Deserialize<AnimStateMachineSerializedModel>(payload);
        cloneModel.ShouldNotBeNull();

        AnimStateMachine clone = new();
        AnimStateMachineSerialization.ApplyModel(clone, cloneModel);

        AssertStateMachinesEquivalent(original, clone);
    }

    private static AnimStateMachine CreateSampleStateMachine()
    {
        AnimationClip idleClip = CreateSampleClip("Idle", "RootIdleRotationX", -0.25f, 0.5f);
        BlendTree1D locomotion = CreateSampleBlendTree();
        locomotion.Name = "Locomotion";

        AnimState idle = new(idleClip, "Idle")
        {
            Position = new Vector2(16.0f, 24.0f),
            StartSecond = 0.1f,
            EndSecond = 0.9f,
            Components =
            [
                new AnimParameterDriverComponent
                {
                    ExecuteLocally = true,
                    ExecuteRemotely = false,
                    DstParameterName = "Speed",
                    SrcParameterName = "Speed",
                    Operation = AnimParameterDriverComponent.EOperation.Add,
                    ConstantValue = 0.25f,
                    RandomMin = 0.0f,
                    RandomMax = 1.0f
                }
            ]
        };

        AnimState locomotionState = new(locomotion, "Locomotion")
        {
            Position = new Vector2(144.0f, 48.0f),
            StartSecond = 0.0f,
            EndSecond = 1.0f,
            Components =
            [
                new TrackingControllerComponent
                {
                    TrackingModeHead = TrackingControllerComponent.ETrackingMode.Animation,
                    TrackingModeLeftHand = TrackingControllerComponent.ETrackingMode.Tracking,
                    TrackingModeRightHand = TrackingControllerComponent.ETrackingMode.Tracking,
                    TrackingModeLeftFoot = TrackingControllerComponent.ETrackingMode.Unchanged,
                    TrackingModeRightFoot = TrackingControllerComponent.ETrackingMode.Unchanged,
                    TrackingModeLeftFingers = TrackingControllerComponent.ETrackingMode.Animation,
                    TrackingModeRightFingers = TrackingControllerComponent.ETrackingMode.Animation,
                    TrackingModeEyes = TrackingControllerComponent.ETrackingMode.Tracking,
                    TrackingModeMouth = TrackingControllerComponent.ETrackingMode.Animation,
                }
            ]
        };

        idle.Transitions =
        [
            new AnimStateTransition
            {
                Name = "IdleToLocomotion",
                DestinationState = locomotionState,
                BlendDuration = 0.2f,
                BlendType = EAnimBlendType.Linear,
                Priority = 3,
                ExitTime = 0.15f,
                FixedDuration = false,
                TransitionOffset = 0.1f,
                InterruptionSource = ETransitionInterruptionSource.Current,
                OrderedInterruption = false,
                CanTransitionToSelf = false,
                Conditions =
                [
                    new AnimTransitionCondition("Speed", AnimTransitionCondition.EComparison.GreaterThan, 0.5f),
                    new AnimTransitionCondition("Grounded", true)
                ]
            }
        ];

        AnimLayer layer = new()
        {
            ApplyType = AnimLayer.EApplyType.Override,
            Weight = 0.75f,
            InitialStateIndex = 0,
            States = [idle, locomotionState]
        };
        layer.AnyState.Position = new Vector2(64.0f, -32.0f);
        layer.AnyState.Transitions =
        [
            new AnimStateTransition
            {
                Name = "AnyToLocomotion",
                DestinationState = locomotionState,
                BlendDuration = 0.35f,
                BlendType = EAnimBlendType.Custom,
                CustomBlendFunction = CreateBlendCurve(),
                Priority = 1,
                ExitTime = 0.0f,
                FixedDuration = true,
                TransitionOffset = 0.0f,
                InterruptionSource = ETransitionInterruptionSource.Next,
                OrderedInterruption = true,
                CanTransitionToSelf = true,
                Conditions =
                [
                    new AnimTransitionCondition("Mode", AnimTransitionCondition.EComparison.Equal, 2.0f)
                ]
            }
        ];

        AnimStateMachine machine = new()
        {
            Name = "HumanoidController",
            OriginalPath = "Assets\\Controllers\\Humanoid.controller",
            OriginalLastWriteTimeUtc = new DateTime(2026, 3, 11, 16, 30, 0, DateTimeKind.Utc),
            AnimatePhysics = true,
            Layers = [layer],
            Variables =
            [
                new KeyValuePair<string, AnimVar>("Grounded", new AnimBool("Grounded", true)),
                new KeyValuePair<string, AnimVar>("Mode", new AnimInt("Mode", 2) { NegativeAllowed = true }),
                new KeyValuePair<string, AnimVar>("Speed", new AnimFloat("Speed", 0.75f) { Smoothing = 0.25f, CompressedBitCount = 16 })
            ]
        };

        return machine;
    }

    private static BlendTree1D CreateSampleBlendTree()
    {
        return new BlendTree1D
        {
            Name = "Locomotion1D",
            ParameterName = "Speed",
            Children =
            [
                new BlendTree1D.Child
                {
                    Motion = CreateSampleClip("Walk", "RootWalkRotationX", -0.5f, 0.75f),
                    Speed = 1.0f,
                    Threshold = 0.0f,
                    HumanoidMirror = false
                },
                new BlendTree1D.Child
                {
                    Motion = CreateSampleClip("Run", "RootRunRotationX", 0.25f, 1.25f),
                    Speed = 1.5f,
                    Threshold = 1.0f,
                    HumanoidMirror = true
                }
            ]
        };
    }

    private static BlendTree2D CreateSampleBlendTree2D()
    {
        return new BlendTree2D
        {
            Name = "Locomotion2D",
            XParameterName = "VelocityX",
            YParameterName = "VelocityY",
            BlendType = BlendTree2D.EBlendType.Directional,
            Children =
            [
                new BlendTree2D.Child
                {
                    Motion = CreateSampleClip("StrafeLeft", "RootStrafeLeftRotationX", -0.75f, -0.1f),
                    PositionX = -1.0f,
                    PositionY = 0.2f,
                    Speed = 0.9f,
                    HumanoidMirror = false
                },
                new BlendTree2D.Child
                {
                    Motion = CreateSampleClip("Forward", "RootForwardRotationX", 0.1f, 0.8f),
                    PositionX = 0.0f,
                    PositionY = 1.0f,
                    Speed = 1.0f,
                    HumanoidMirror = false
                },
                new BlendTree2D.Child
                {
                    Motion = CreateSampleClip("StrafeRight", "RootStrafeRightRotationX", 0.3f, 1.1f),
                    PositionX = 1.0f,
                    PositionY = 0.15f,
                    Speed = 1.1f,
                    HumanoidMirror = true
                }
            ]
        };
    }

    private static BlendTreeDirect CreateSampleBlendTreeDirect()
    {
        return new BlendTreeDirect
        {
            Name = "DirectMixer",
            Children =
            [
                new BlendTreeDirect.Child
                {
                    Motion = CreateSampleClip("UpperBodyAim", "RootUpperBodyAimRotationX", -0.15f, 0.45f),
                    WeightParameterName = "AimWeight",
                    Speed = 1.0f,
                    HumanoidMirror = false
                },
                new BlendTreeDirect.Child
                {
                    Motion = CreateSampleBlendTree(),
                    WeightParameterName = "LocomotionWeight",
                    Speed = 0.85f,
                    HumanoidMirror = false
                },
                new BlendTreeDirect.Child
                {
                    Motion = CreateSampleClip("AdditiveLean", "RootAdditiveLeanRotationX", -0.05f, 0.2f),
                    WeightParameterName = null,
                    Speed = 1.2f,
                    HumanoidMirror = true
                }
            ]
        };
    }

    private static AnimationClip CreateSampleClip(string name, string animationName, float firstValue, float secondValue)
    {
        PropAnimFloat animation = new(24, 24.0f, looped: true, useKeyframes: true)
        {
            Name = animationName
        };
        animation.Keyframes.Add(
            new FloatKeyframe(0.0f, firstValue, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, secondValue, 0.0f, EVectorInterpType.Linear));

        AnimationMember root = new("Root", EAnimationMemberType.Group);
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        transform.Children.Add(new AnimationMember("QuaternionX", EAnimationMemberType.Property, animation));
        root.Children.Add(transform);

        return new AnimationClip(root)
        {
            Name = name,
            OriginalPath = $"Assets\\Walks\\{name}.anim",
            OriginalLastWriteTimeUtc = new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc),
            TraversalMethod = EAnimTreeTraversalMethod.BreadthFirst,
            Looped = true,
            ClipKind = EAnimationClipKind.UnityHumanoidMuscle,
            HasMuscleChannels = true,
            HasRootMotion = false,
            HasIKGoals = false,
            SampleRate = 24,
            LengthInSeconds = 1.0f
        };
    }

    private static PropAnimFloat CreateBlendCurve()
    {
        PropAnimFloat curve = new(24, 24.0f, looped: false, useKeyframes: true)
        {
            Name = "BlendCurve"
        };
        curve.Keyframes.Add(
            new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Linear));
        return curve;
    }

    private static void AssertStateMachinesEquivalent(AnimStateMachine expected, AnimStateMachine actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.OriginalPath.ShouldBe(expected.OriginalPath);
        actual.OriginalLastWriteTimeUtc.ShouldBe(expected.OriginalLastWriteTimeUtc);
        actual.AnimatePhysics.ShouldBe(expected.AnimatePhysics);

        actual.Variables.Count.ShouldBe(expected.Variables.Count);
        foreach (var expectedVariable in expected.Variables.OrderBy(static x => x.Key))
        {
            actual.Variables.ContainsKey(expectedVariable.Key).ShouldBeTrue();
            AssertVariablesEquivalent(expectedVariable.Value, actual.Variables[expectedVariable.Key]);
        }

        actual.Layers.Count.ShouldBe(expected.Layers.Count);
        for (int i = 0; i < expected.Layers.Count; i++)
            AssertLayersEquivalent(expected.Layers[i], actual.Layers[i]);
    }

    private static void AssertLayersEquivalent(AnimLayer expected, AnimLayer actual)
    {
        actual.ApplyType.ShouldBe(expected.ApplyType);
        actual.Weight.ShouldBe(expected.Weight);
        actual.InitialStateIndex.ShouldBe(expected.InitialStateIndex);
        actual.AnyState.Position.ShouldBe(expected.AnyState.Position);
        actual.AnyState.Transitions.Count.ShouldBe(expected.AnyState.Transitions.Count);
        for (int i = 0; i < expected.AnyState.Transitions.Count; i++)
            AssertTransitionsEquivalent(expected.AnyState.Transitions[i], actual.AnyState.Transitions[i], expected, actual);

        actual.States.Count.ShouldBe(expected.States.Count);
        for (int i = 0; i < expected.States.Count; i++)
            AssertStatesEquivalent(expected.States[i], actual.States[i], expected, actual);
    }

    private static void AssertStatesEquivalent(AnimState expected, AnimState actual, AnimLayer expectedLayer, AnimLayer actualLayer)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.Position.ShouldBe(expected.Position);
        actual.StartSecond.ShouldBe(expected.StartSecond);
        actual.EndSecond.ShouldBe(expected.EndSecond);
        actual.Components.Count.ShouldBe(expected.Components.Count);
        for (int i = 0; i < expected.Components.Count; i++)
            AssertComponentsEquivalent(expected.Components[i], actual.Components[i]);

        AssertMotionsEquivalent(expected.Motion, actual.Motion);

        actual.Transitions.Count.ShouldBe(expected.Transitions.Count);
        for (int i = 0; i < expected.Transitions.Count; i++)
            AssertTransitionsEquivalent(expected.Transitions[i], actual.Transitions[i], expectedLayer, actualLayer);
    }

    private static void AssertTransitionsEquivalent(AnimStateTransition expected, AnimStateTransition actual, AnimLayer expectedLayer, AnimLayer actualLayer)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.BlendDuration.ShouldBe(expected.BlendDuration);
        actual.BlendType.ShouldBe(expected.BlendType);
        actual.Priority.ShouldBe(expected.Priority);
        actual.ExitTime.ShouldBe(expected.ExitTime);
        actual.FixedDuration.ShouldBe(expected.FixedDuration);
        actual.TransitionOffset.ShouldBe(expected.TransitionOffset);
        actual.InterruptionSource.ShouldBe(expected.InterruptionSource);
        actual.OrderedInterruption.ShouldBe(expected.OrderedInterruption);
        actual.CanTransitionToSelf.ShouldBe(expected.CanTransitionToSelf);
        expectedLayer.States.IndexOf(expected.DestinationState!).ShouldBeGreaterThanOrEqualTo(0);
        actualLayer.States.IndexOf(actual.DestinationState!).ShouldBe(expectedLayer.States.IndexOf(expected.DestinationState!));

        if (expected.CustomBlendFunction is null)
        {
            actual.CustomBlendFunction.ShouldBeNull();
        }
        else
        {
            actual.CustomBlendFunction.ShouldNotBeNull();
            AssertFloatAnimationsEquivalent(expected.CustomBlendFunction, actual.CustomBlendFunction!);
        }

        actual.Conditions.Count.ShouldBe(expected.Conditions.Count);
        for (int i = 0; i < expected.Conditions.Count; i++)
        {
            actual.Conditions[i].ParameterName.ShouldBe(expected.Conditions[i].ParameterName);
            actual.Conditions[i].Comparison.ShouldBe(expected.Conditions[i].Comparison);
            actual.Conditions[i].ComparisonBool.ShouldBe(expected.Conditions[i].ComparisonBool);
            actual.Conditions[i].ComparisonInt.ShouldBe(expected.Conditions[i].ComparisonInt);
            actual.Conditions[i].ComparisonFloat.ShouldBe(expected.Conditions[i].ComparisonFloat);
        }
    }

    private static void AssertVariablesEquivalent(AnimVar expected, AnimVar actual)
    {
        actual.ParameterName.ShouldBe(expected.ParameterName);
        actual.GetType().ShouldBe(expected.GetType());

        switch (expected)
        {
            case AnimBool expectedBool:
                actual.ShouldBeOfType<AnimBool>().Value.ShouldBe(expectedBool.Value);
                break;
            case AnimInt expectedInt:
            {
                AnimInt actualInt = actual.ShouldBeOfType<AnimInt>();
                actualInt.Value.ShouldBe(expectedInt.Value);
                actualInt.NegativeAllowed.ShouldBe(expectedInt.NegativeAllowed);
                break;
            }
            case AnimFloat expectedFloat:
            {
                AnimFloat actualFloat = actual.ShouldBeOfType<AnimFloat>();
                actualFloat.Value.ShouldBe(expectedFloat.Value);
                actualFloat.Smoothing.ShouldBe(expectedFloat.Smoothing);
                actualFloat.CompressedBitCount.ShouldBe(expectedFloat.CompressedBitCount);
                break;
            }
        }
    }

    private static void AssertComponentsEquivalent(AnimStateComponent expected, AnimStateComponent actual)
    {
        actual.GetType().ShouldBe(expected.GetType());

        switch (expected)
        {
            case AnimParameterDriverComponent expectedDriver:
            {
                AnimParameterDriverComponent actualDriver = actual.ShouldBeOfType<AnimParameterDriverComponent>();
                actualDriver.ExecuteLocally.ShouldBe(expectedDriver.ExecuteLocally);
                actualDriver.ExecuteRemotely.ShouldBe(expectedDriver.ExecuteRemotely);
                actualDriver.DstParameterName.ShouldBe(expectedDriver.DstParameterName);
                actualDriver.SrcParameterName.ShouldBe(expectedDriver.SrcParameterName);
                actualDriver.Operation.ShouldBe(expectedDriver.Operation);
                actualDriver.ConstantValue.ShouldBe(expectedDriver.ConstantValue);
                actualDriver.RandomMin.ShouldBe(expectedDriver.RandomMin);
                actualDriver.RandomMax.ShouldBe(expectedDriver.RandomMax);
                break;
            }
            case TrackingControllerComponent expectedTracking:
            {
                TrackingControllerComponent actualTracking = actual.ShouldBeOfType<TrackingControllerComponent>();
                actualTracking.TrackingModeHead.ShouldBe(expectedTracking.TrackingModeHead);
                actualTracking.TrackingModeLeftHand.ShouldBe(expectedTracking.TrackingModeLeftHand);
                actualTracking.TrackingModeRightHand.ShouldBe(expectedTracking.TrackingModeRightHand);
                actualTracking.TrackingModeLeftFoot.ShouldBe(expectedTracking.TrackingModeLeftFoot);
                actualTracking.TrackingModeRightFoot.ShouldBe(expectedTracking.TrackingModeRightFoot);
                actualTracking.TrackingModeLeftFingers.ShouldBe(expectedTracking.TrackingModeLeftFingers);
                actualTracking.TrackingModeRightFingers.ShouldBe(expectedTracking.TrackingModeRightFingers);
                actualTracking.TrackingModeEyes.ShouldBe(expectedTracking.TrackingModeEyes);
                actualTracking.TrackingModeMouth.ShouldBe(expectedTracking.TrackingModeMouth);
                break;
            }
        }
    }

    private static void AssertBlendTreesEquivalent(BlendTree1D expected, BlendTree1D actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.ParameterName.ShouldBe(expected.ParameterName);
        actual.Children.Count.ShouldBe(expected.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            actual.Children[i].Speed.ShouldBe(expected.Children[i].Speed);
            actual.Children[i].Threshold.ShouldBe(expected.Children[i].Threshold);
            actual.Children[i].HumanoidMirror.ShouldBe(expected.Children[i].HumanoidMirror);
            AssertMotionsEquivalent(expected.Children[i].Motion, actual.Children[i].Motion);
        }
    }

    private static void AssertBlendTreesEquivalent(BlendTree2D expected, BlendTree2D actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.XParameterName.ShouldBe(expected.XParameterName);
        actual.YParameterName.ShouldBe(expected.YParameterName);
        actual.BlendType.ShouldBe(expected.BlendType);
        actual.Children.Count.ShouldBe(expected.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            actual.Children[i].PositionX.ShouldBe(expected.Children[i].PositionX);
            actual.Children[i].PositionY.ShouldBe(expected.Children[i].PositionY);
            actual.Children[i].Speed.ShouldBe(expected.Children[i].Speed);
            actual.Children[i].HumanoidMirror.ShouldBe(expected.Children[i].HumanoidMirror);
            AssertMotionsEquivalent(expected.Children[i].Motion, actual.Children[i].Motion);
        }
    }

    private static void AssertBlendTreesEquivalent(BlendTreeDirect expected, BlendTreeDirect actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.Children.Count.ShouldBe(expected.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            actual.Children[i].WeightParameterName.ShouldBe(expected.Children[i].WeightParameterName);
            actual.Children[i].Speed.ShouldBe(expected.Children[i].Speed);
            actual.Children[i].HumanoidMirror.ShouldBe(expected.Children[i].HumanoidMirror);
            AssertMotionsEquivalent(expected.Children[i].Motion, actual.Children[i].Motion);
        }
    }

    private static void AssertMotionsEquivalent(MotionBase? expected, MotionBase? actual)
    {
        if (expected is null || actual is null)
        {
            actual.ShouldBe(expected);
            return;
        }

        actual.GetType().ShouldBe(expected.GetType());

        switch (expected)
        {
            case AnimationClip expectedClip:
                AssertClipsEquivalent(expectedClip, actual.ShouldBeOfType<AnimationClip>());
                break;
            case BlendTree1D expectedBlendTree:
                AssertBlendTreesEquivalent(expectedBlendTree, actual.ShouldBeOfType<BlendTree1D>());
                break;
            case BlendTree2D expectedBlendTree2D:
                AssertBlendTreesEquivalent(expectedBlendTree2D, actual.ShouldBeOfType<BlendTree2D>());
                break;
            case BlendTreeDirect expectedBlendTreeDirect:
                AssertBlendTreesEquivalent(expectedBlendTreeDirect, actual.ShouldBeOfType<BlendTreeDirect>());
                break;
            default:
                throw new NotSupportedException($"Unsupported motion type '{expected.GetType().FullName}'.");
        }
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
                AssertFloatAnimationsEquivalent(expectedFloat, actual.Animation.ShouldBeOfType<PropAnimFloat>());
        }

        for (int i = 0; i < expected.Children.Count; i++)
            AssertMembersEquivalent(expected.Children[i], actual.Children[i]);
    }

    private static void AssertFloatAnimationsEquivalent(PropAnimFloat expected, PropAnimFloat actual)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.AuthoredCadence.ShouldBe(expected.AuthoredCadence);
        actual.Looped.ShouldBe(expected.Looped);
        actual.Keyframes.Count.ShouldBe(expected.Keyframes.Count);

        for (int i = 0; i < expected.Keyframes.Count; i++)
        {
            FloatKeyframe expectedFrame = expected.Keyframes[i];
            FloatKeyframe actualFrame = actual.Keyframes[i];
            actualFrame.Second.ShouldBe(expectedFrame.Second);
            actualFrame.InValue.ShouldBe(expectedFrame.InValue);
            actualFrame.OutValue.ShouldBe(expectedFrame.OutValue);
            actualFrame.InterpolationTypeIn.ShouldBe(expectedFrame.InterpolationTypeIn);
            actualFrame.InterpolationTypeOut.ShouldBe(expectedFrame.InterpolationTypeOut);
        }
    }
}