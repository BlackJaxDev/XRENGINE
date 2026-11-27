using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Animation.MotionBricks;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.MotionBricks;

[TestFixture]
public sealed class MotionBricksStateMachineAndConversionTests
{
    private static readonly MethodInfo EvaluateAndApplyMethod =
        typeof(AnimStateMachineComponent).GetMethod("EvaluateAndApply", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimStateMachineComponent.EvaluateAndApply.");

    private static readonly MethodInfo PublishRootMotionMethod =
        typeof(AnimStateMachineComponent).GetMethod("PublishRootMotion", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimStateMachineComponent.PublishRootMotion.");

    private static readonly MethodInfo TryPreparePlaybackMethod =
        typeof(AnimStateMachineComponent).GetMethod("TryPrepareStateMachinePlayback", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate AnimStateMachineComponent.TryPrepareStateMachinePlayback.");

    private static readonly MethodInfo ApplyMusclePoseMethod =
        typeof(HumanoidComponent).GetMethod("ApplyMusclePose", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate HumanoidComponent.ApplyMusclePose.");

    [Test]
    public void SomaToHumanoidMuscleConverter_NeutralPoseProducesZeroMusclesAndScaledBody()
    {
        var converter = new SomaToHumanoidMuscleConverter();
        float[] muscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        SomaPoseTransform[] neutral = SomaRestSkeletonCalibration.CanonicalDefault.ReferenceLocalPoses;

        converter.ConvertFrame(
            neutral,
            avatarLegScale: 1.25f,
            muscles,
            out Vector3 bodyPosition,
            out Quaternion bodyRotation);

        foreach (EHumanoidValue channel in MotionBricksLocomotionMotion.DrivenMuscleChannels)
            muscles[(int)channel].ShouldBe(0.0f, 1.0e-5f);

        bodyPosition.Y.ShouldBe(SomaRestSkeletonCalibration.ReferenceHipsHeightMeters * 1.25f, 1.0e-4f);
        MathF.Abs(Quaternion.Dot(bodyRotation, Quaternion.Identity)).ShouldBeGreaterThan(0.99999f);
    }

    [Test]
    public void SomaToHumanoidMuscleConverter_MapsJointRotationsToSignedCanonicalMuscles()
    {
        var converter = new SomaToHumanoidMuscleConverter();
        float[] muscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        SomaPoseTransform[] pose = [.. SomaRestSkeletonCalibration.CanonicalDefault.ReferenceLocalPoses];

        // Spine1: +20 deg around +X -> frontBackDeg=+20 -> NativePoseSolver frontBackSign=-1 -> normalized = -20 / 40 = -0.5.
        pose[1] = new SomaPoseTransform(pose[1].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20.0f * MathF.PI / 180.0f));

        // LeftLeg (index 22): +25 deg around +X -> -frontBackDeg = -25 -> UpperLegFrontBackRange (90, -50) -> +0.5.
        pose[22] = new SomaPoseTransform(pose[22].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 25.0f * MathF.PI / 180.0f));

        // LeftShin (index 23): -40 deg around +X -> -frontBackDeg = +40 -> LowerLegStretchRange (-80, 80) -> +0.5.
        pose[23] = new SomaPoseTransform(pose[23].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -40.0f * MathF.PI / 180.0f));

        // Head (index 6): +20 deg around +Y -> twistDeg = +20 -> HeadTurnLeftRight (+40) -> +0.5.
        pose[6] = new SomaPoseTransform(pose[6].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20.0f * MathF.PI / 180.0f));

        converter.ConvertFrame(pose, avatarLegScale: 1.0f, muscles, out _, out _);

        muscles[(int)EHumanoidValue.SpineFrontBack].ShouldBe(-0.5f, 1.0e-3f);
        muscles[(int)EHumanoidValue.LeftUpperLegFrontBack].ShouldBe(0.5f, 1.0e-3f);
        muscles[(int)EHumanoidValue.LeftLowerLegStretch].ShouldBe(0.5f, 1.0e-3f);
        muscles[(int)EHumanoidValue.HeadTurnLeftRight].ShouldBe(0.5f, 1.0e-3f);
    }

    [Test]
    public void AnimStateMachineComponent_MotionBricksState_DrivesHumanoidMusclesAndPublishesRootMotion()
    {
        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid, out SceneNode leftLegNode);
        var stateMachineComp = root.AddComponent<AnimStateMachineComponent>()!;
        stateMachineComp.Humanoid = humanoid;
        stateMachineComp.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer;

        var syntheticSession = new SyntheticWalkSomaSession(stepMetersPerFrame: 0.06f, legSwingRadians: 0.45f);
        using var driver = new MotionBricksAsyncLocomotionDriver(
            syntheticSession,
            ownsSession: false,
            useBackgroundThread: false);
        var motion = new MotionBricksLocomotionMotion
        {
            Name = "MotionBricksLocomotion",
            RuntimeDriverOverride = driver,
        };

        var state = new AnimState(motion, "Locomotion");
        var layer = new AnimLayer(state) { InitialState = state };
        stateMachineComp.StateMachine.Layers.Add(layer);
        stateMachineComp.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 1.0f);

        int rootMotionEvents = 0;
        Vector3 accumulatedDelta = Vector3.Zero;
        stateMachineComp.RootMotionEvaluated += (_, delta) =>
        {
            rootMotionEvents++;
            accumulatedDelta += delta.Translation;
        };

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(stateMachineComp, null)!;
        prepared.ShouldBeTrue(stateMachineComp.PlaybackCapabilityDiagnostic);

        long frameTicks = Stopwatch.Frequency / 30;
        float maxAbsLeftLegMuscle = 0.0f;
        float minLegBindRotationDot = 1.0f;
        Transform legTransform = leftLegNode.GetTransformAs<Transform>(true)!;

        for (int i = 0; i < 10; i++)
        {
            EvaluateAndApplyMethod.Invoke(stateMachineComp, [frameTicks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(stateMachineComp, null);

            if (humanoid.TryGetMuscleValue(EHumanoidValue.LeftUpperLegFrontBack, out float sampleLegMuscle))
                maxAbsLeftLegMuscle = MathF.Max(maxAbsLeftLegMuscle, MathF.Abs(sampleLegMuscle));

            float bindDot = MathF.Abs(Quaternion.Dot(
                Quaternion.Normalize(legTransform.Rotation),
                Quaternion.Normalize(legTransform.BindState.Rotation)));
            minLegBindRotationDot = MathF.Min(minLegBindRotationDot, bindDot);
        }

        syntheticSession.InferenceCallCount.ShouldBeGreaterThanOrEqualTo(1);
        stateMachineComp.RootMotionContributorCount.ShouldBe(1);
        rootMotionEvents.ShouldBeGreaterThan(1);
        accumulatedDelta.Length().ShouldBeGreaterThan(0.15f);

        humanoid.TryGetMuscleValue(EHumanoidValue.LeftUpperLegFrontBack, out _).ShouldBeTrue();
        maxAbsLeftLegMuscle.ShouldBeGreaterThan(0.05f);
        minLegBindRotationDot.ShouldBeLessThan(0.999f);
    }

    [Test]
    public void AnimStateMachine_MotionBricksSharesExactSlotsAndBlendsWithTraditionalClip()
    {
        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid, out _);
        var stateMachineComp = root.AddComponent<AnimStateMachineComponent>()!;
        stateMachineComp.Humanoid = humanoid;

        // MotionBricks state writes SpineFrontBack = 0.0.
        var constantDriver = new ConstantMuscleTestDriver(EHumanoidValue.SpineFrontBack, 0.0f);
        var motionBricks = new MotionBricksLocomotionMotion
        {
            Name = "NeuralLocomotion",
            RuntimeDriverOverride = constantDriver,
        };

        // Traditional clip writes SpineFrontBack = 1.0 using the exact Unity humanoid binding shape.
        AnimationClip traditionalClip = CreateConstantHumanoidMuscleClip(EHumanoidValue.SpineFrontBack, 1.0f);

        var neuralState = new AnimState(motionBricks, "Locomotion");
        var clipState = new AnimState(traditionalClip, "Bow");

        var triggerVar = new AnimBool("DoBow", false);
        var transition = new AnimStateTransition
        {
            DestinationState = clipState,
            HasExitTime = false,
            BlendDuration = 1.0f,
            Conditions = [new AnimTransitionCondition("DoBow", isThisState: true)],
        };
        neuralState.Transitions.Add(transition);

        var layer = new AnimLayer(neuralState, clipState) { InitialState = neuralState };
        stateMachineComp.StateMachine.Layers.Add(layer);
        stateMachineComp.StateMachine.Variables["DoBow"] = triggerVar;

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(stateMachineComp, null)!;
        prepared.ShouldBeTrue(stateMachineComp.PlaybackCapabilityDiagnostic);

        // Verify shared slot assignment between MotionBricks and AnimationClip on SetImportedRawValue.
        // MotionBase registers both GetComponentInHierarchy and SetImportedRawValue method nodes.
        motionBricks.AnimatedCurves.Keys.Count(k => k.Contains("SetImportedRawValue", StringComparison.Ordinal))
            .ShouldBe(MotionBricksLocomotionMotion.DrivenMuscleChannels.Length);
        string clipPath = traditionalClip.AnimatedCurves.Keys.Single(k => k.Contains("SetImportedRawValue", StringComparison.Ordinal));
        motionBricks.AnimatedCurves.ContainsKey(clipPath).ShouldBeTrue();
        motionBricks.AnimatedCurves[clipPath].Slot.ShouldBe(traditionalClip.AnimatedCurves[clipPath].Slot);

        // Frame 1: pure MotionBricks state => SpineFrontBack ~ 0.0.
        EvaluateAndApplyMethod.Invoke(stateMachineComp, [Stopwatch.Frequency / 60, humanoid]);
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float initialValue).ShouldBeTrue();
        initialValue.ShouldBe(0.0f, 1.0e-4f);

        // Trigger 1.0s cross-fade and advance 30 frames at 60 Hz (~0.5s) => blended value ~ 0.5.
        triggerVar.BoolValue = true;
        long frame60Ticks = Stopwatch.Frequency / 60;
        for (int i = 0; i < 30; i++)
            EvaluateAndApplyMethod.Invoke(stateMachineComp, [frame60Ticks, humanoid]);
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float blendedValue).ShouldBeTrue();
        blendedValue.ShouldBeGreaterThan(0.35f);
        blendedValue.ShouldBeLessThan(0.65f);
    }

    [Test]
    public void MotionBricksLocomotionMotion_RoundTripsThroughMotionSerialization()
    {
        var original = new MotionBricksLocomotionMotion
        {
            Name = "HeroLocomotion",
            ModelProfileId = "soma-rp-v1",
            LocomotionPresetId = "locomotion_bs1_linear",
            MoveXParameter = "StickX",
            MoveYParameter = "StickY",
            DesiredFacingYawParameter = "LookYaw",
            StrafeModeParameter = "Aiming",
            StyleParameter = "GaitStyle",
            IsGroundedParameter = "Grounded",
            WalkSpeedMetersPerSecond = 1.8f,
            RunSpeedMetersPerSecond = 5.1f,
            ReplanIntervalSeconds = 0.1f,
            InputReplanThreshold = 0.2f,
            BakeOrientationIntoPose = true,
            BakePositionYIntoPose = false,
            BakePositionXZIntoPose = true,
        };

        var stateMachine = new AnimStateMachine();
        var state = new AnimState(original, "Locomotion");
        stateMachine.Layers.Add(new AnimLayer(state) { InitialState = state });

        byte[] bytes = MemoryPack.MemoryPackSerializer.Serialize(AnimStateMachineSerialization.CreateModel(stateMachine));
        var model = MemoryPack.MemoryPackSerializer.Deserialize<AnimStateMachineSerializedModel>(bytes);
        var restored = new AnimStateMachine();
        AnimStateMachineSerialization.ApplyModel(restored, model);

        restored.Layers.Count.ShouldBe(1);
        restored.Layers[0].States.Count.ShouldBe(1);
        var restoredMotion = restored.Layers[0].States[0].Motion.ShouldBeOfType<MotionBricksLocomotionMotion>();
        restoredMotion.Name.ShouldBe("HeroLocomotion");
        restoredMotion.LocomotionPresetId.ShouldBe("locomotion_bs1_linear");
        restoredMotion.MoveXParameter.ShouldBe("StickX");
        restoredMotion.MoveYParameter.ShouldBe("StickY");
        restoredMotion.WalkSpeedMetersPerSecond.ShouldBe(1.8f, 1.0e-5f);
        restoredMotion.RunSpeedMetersPerSecond.ShouldBe(5.1f, 1.0e-5f);
        restoredMotion.BakeOrientationIntoPose.ShouldBeTrue();
        restoredMotion.BakePositionYIntoPose.ShouldBeFalse();
        restoredMotion.BakePositionXZIntoPose.ShouldBeTrue();
    }

    private static SceneNode CreateTestHumanoidRig(out HumanoidComponent humanoid, out SceneNode leftLegNode)
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(translation: new(0.0f, 1.0f, 0.0f)));
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        leftLegNode = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLegNode, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
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

        humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();
        return root;
    }

    private static void SaveBindPoseRecursive(SceneNode node)
    {
        node.Transform.SaveBindState();
        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is not null)
                SaveBindPoseRecursive(child.SceneNode);
        }
    }

    private static AnimationClip CreateConstantHumanoidMuscleClip(EHumanoidValue muscle, float value)
    {
        var clip = new AnimationClip
        {
            Name = "ConstantMuscleClip",
            LengthInSeconds = 1.0f,
            Looped = true,
        };
        var anim = new PropAnimFloat
        {
            LengthInSeconds = 1.0f,
            Looped = true,
            DefaultValue = value,
        };
        anim.Keyframes.Add(new FloatKeyframe(0.0f, value, 0.0f, EVectorInterpType.Linear));
        anim.Keyframes.Add(new FloatKeyframe(1.0f, value, 0.0f, EVectorInterpType.Linear));

        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var getHumanoid = new AnimationMember("GetComponentInHierarchy", EAnimationMemberType.Method)
        {
            MethodArguments = ["HumanoidComponent"],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        var setter = new AnimationMember("SetImportedRawValue", EAnimationMemberType.Method)
        {
            MethodArguments = [muscle, 0.0f, false],
            AnimatedMethodArgumentIndex = 1,
            CacheReturnValue = false,
            DefaultValue = 0.0f,
            Animation = anim,
        };
        getHumanoid.Children.Add(setter);
        sceneNode.Children.Add(getHumanoid);
        clip.RootMember ??= new AnimationMember(string.Empty, EAnimationMemberType.Group);
        clip.RootMember.Children.Add(sceneNode);
        return clip;
    }

    private sealed class SyntheticWalkSomaSession(float stepMetersPerFrame, float legSwingRadians) : IMotionBricksInferenceSession
    {
        public int InferenceCallCount { get; private set; }
        public bool IsEngineReady => true;

        public bool TryBuildOrLoadEngine(out bool loadedFromCache, out string? error)
        {
            loadedFromCache = true;
            error = null;
            return true;
        }

        public bool TryRunSomaInference(SomaInferenceInputBuffer input, SomaInferenceOutputBuffer output, out string? error)
        {
            InferenceCallCount++;
            Array.Clear(output.PredGlobalMotions);
            output.PredNumTokens[0] = 6; // 18 valid frames (raw frames 3..20).

            int rotOffset = SomaModelContract.GlobalRootFeatureCount + SomaModelContract.LocalPosePositionFeatureCount;
            int contactOffset = SomaModelContract.GlobalMotionFeatureCount - SomaModelContract.FootContactCount;

            for (int rawFrame = 0; rawFrame < 24; rawFrame++)
            {
                int frame = rawFrame - SomaModelContract.BoundaryFrameCount;
                int rawBase = rawFrame * SomaModelContract.GlobalMotionFeatureCount;
                float progress = Math.Max(0, frame);

                // Move forward along aligned +X (matches default headingAlignment (1, 0)).
                output.PredGlobalMotions[rawBase + 0] = progress * stepMetersPerFrame;
                output.PredGlobalMotions[rawBase + 1] = SomaRestSkeletonCalibration.ReferenceHipsHeightMeters;
                output.PredGlobalMotions[rawBase + 2] = 0.0f;

                float phase = progress * 0.4f;
                Quaternion leftLegRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.Sin(phase) * legSwingRadians);
                Quaternion rightLegRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.Sin(phase) * legSwingRadians);

                for (int joint = 0; joint < SomaModelContract.JointCount; joint++)
                {
                    Quaternion globalRot = joint switch
                    {
                        22 => leftLegRot,
                        26 => rightLegRot,
                        _ => Quaternion.Identity,
                    };
                    SomaTensorCodec.RotationTo6D(globalRot, out Vector3 xAxis, out Vector3 yAxis);
                    int dst = rawBase + rotOffset + joint * 6;
                    output.PredGlobalMotions[dst + 0] = xAxis.X;
                    output.PredGlobalMotions[dst + 1] = xAxis.Y;
                    output.PredGlobalMotions[dst + 2] = xAxis.Z;
                    output.PredGlobalMotions[dst + 3] = yAxis.X;
                    output.PredGlobalMotions[dst + 4] = yAxis.Y;
                    output.PredGlobalMotions[dst + 5] = yAxis.Z;
                }

                output.PredGlobalMotions[rawBase + contactOffset + 0] = 1.0f;
                output.PredGlobalMotions[rawBase + contactOffset + 2] = 0.0f;
            }

            error = null;
            return true;
        }

        public void Dispose() { }
    }

    private sealed class ConstantMuscleTestDriver(EHumanoidValue muscle, float value) : IMotionBricksLocomotionDriver
    {
        public bool IsReady => true;
        public string DiagnosticStatus => "Ready (Constant Test Driver)";
        public void OnStateEntered() { }
        public void OnStateExited() { }
        public void ResetPlayback() { }

        public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination)
        {
            Array.Clear(destination.MuscleValues);
            destination.MuscleValues[(int)muscle] = value;
            destination.CanonicalBodyPosition = new Vector3(0.0f, 1.0f, 0.0f);
            destination.CanonicalBodyRotation = Quaternion.Identity;
            destination.CurrentBodyPosition = new Vector3(0.0f, 1.0f, 0.0f);
            destination.CurrentBodyRotation = Quaternion.Identity;
            destination.HasValidPose = true;
        }

        public void Dispose() { }
    }
}
