using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.MotionBricks;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.MotionBricks;

[TestFixture]
public sealed class MotionBricksPlannerAndAsyncDriverTests
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
    public void CriticalSpringDamper_ConvergesSmoothlyAndWrapsShortestAngle()
    {
        float value = 0.0f;
        float velocity = 0.0f;
        for (int i = 0; i < 120; i++)
            CriticalSpringDamper.StepScalar(ref value, ref velocity, goal: 4.0f, halflifeSeconds: 0.2f, deltaTime: 1.0f / 60.0f);

        value.ShouldBe(4.0f, 0.05f);
        MathF.Abs(velocity).ShouldBeLessThan(0.2f);

        // Angle +170 deg -> -170 deg must cross +-180 deg (+20 deg delta), never sweeping through 0.
        float angle = 170.0f * MathF.PI / 180.0f;
        float angularVel = 0.0f;
        float goal = -170.0f * MathF.PI / 180.0f;
        for (int i = 0; i < 60; i++)
        {
            CriticalSpringDamper.StepAngle(ref angle, ref angularVel, goal, halflifeSeconds: 0.15f, deltaTime: 1.0f / 60.0f);
            MathF.Abs(angle).ShouldBeGreaterThan(MathF.PI * 0.85f);
        }

        float finalError = MathF.Abs(CriticalSpringDamper.WrapAngleRadians(angle - goal));
        finalError.ShouldBeLessThan(0.05f);
    }

    [Test]
    public void SmartLocomotionPlanner_IdleDeadzoneWalkRunAndSharpTurnReplan()
    {
        var planner = new SomaSmartLocomotionPlanner();
        var targets = new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * SomaModelContract.JointCount];

        // Sub-deadzone stick input keeps idle locked.
        MotionBricksSteeringInput tinyStick = CreateSteering(new Vector2(0.03f, 0.02f), dt: 1.0f / 60.0f);
        planner.UpdateSteering(tinyStick);
        planner.IsIdleLocked.ShouldBeTrue();
        planner.DesiredSpeed.ShouldBe(0.0f);

        // Full stick forward ramps up toward run speed.
        MotionBricksSteeringInput forwardRun = CreateSteering(new Vector2(0.0f, 1.0f), dt: 1.0f / 30.0f);
        for (int i = 0; i < 45; i++)
            planner.UpdateSteering(forwardRun);

        planner.IsIdleLocked.ShouldBeFalse();
        planner.DesiredSpeed.ShouldBeGreaterThan(3.0f);
        SomaInferenceConfig steadyConfig = planner.BuildTargetPoses(
            forwardRun,
            new Vector3(0.0f, 1.0f, 0.0f),
            isUrgentTurnOrStop: false,
            targets,
            out int steadyTargetCount);
        steadyTargetCount.ShouldBe(SomaModelContract.TargetPoseCapacity);
        steadyConfig.IsTokenCountAllowed(14).ShouldBeTrue();

        // Sharp 180-degree reversal triggers urgent replan and shorter token horizon (6..10).
        MotionBricksSteeringInput reverseStick = CreateSteering(new Vector2(0.0f, -1.0f), dt: 0.05f);
        planner.UpdateSteering(reverseStick);
        bool shouldReplan = planner.ShouldTriggerReplan(reverseStick, remainingBufferFrames: 30, out bool isUrgent);
        shouldReplan.ShouldBeTrue();
        isUrgent.ShouldBeTrue();

        SomaInferenceConfig urgentConfig = planner.BuildTargetPoses(
            reverseStick,
            new Vector3(0.0f, 1.0f, 0.0f),
            isUrgent,
            targets,
            out _);
        urgentConfig.IsTokenCountAllowed(6).ShouldBeTrue();
        urgentConfig.IsTokenCountAllowed(14).ShouldBeFalse();
    }

    [Test]
    public void SmartLocomotionPlanner_StrafeModeSeparatesFacingFromTravelDirection()
    {
        var planner = new SomaSmartLocomotionPlanner();
        var targets = new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * SomaModelContract.JointCount];

        // Face +Z (yaw = 0) while strafing right (+X).
        MotionBricksSteeringInput strafeRight = CreateSteering(
            moveInput: new Vector2(1.0f, 0.0f),
            dt: 1.0f / 30.0f,
            strafeMode: true,
            desiredFacingYaw: 0.0f);

        for (int i = 0; i < 40; i++)
            planner.UpdateSteering(strafeRight);

        MathF.Abs(planner.CurrentFacingYawRadians).ShouldBeLessThan(0.05f);
        MathF.Abs(CriticalSpringDamper.WrapAngleRadians(planner.DesiredVelocityYawRadians - MathF.PI * 0.5f))
            .ShouldBeLessThan(0.15f);

        planner.BuildTargetPoses(
            strafeRight,
            Vector3.Zero,
            isUrgentTurnOrStop: false,
            targets,
            out int targetCount);
        targetCount.ShouldBe(SomaModelContract.TargetPoseCapacity);
        targets[0].Position.X.ShouldBeGreaterThan(0.5f);
    }

    [Test]
    public void ContextHistoryBuffer_RecordsAt60HzResamplesAt30HzAndAppliesRootClamp()
    {
        var history = new SomaContextHistoryBuffer();
        SomaPoseTransform[] framePose = [.. SomaRestSkeletonCalibration.CanonicalDefault.ReferenceLocalPoses];

        // Record 12 frames at 60 Hz moving at 3.0 m/s along +Z (0.05 m per 60 Hz frame, 0.10 m per 30 Hz frame).
        for (int i = 0; i < 12; i++)
        {
            framePose[0] = new SomaPoseTransform(new Vector3(0.0f, 1.0f, i * 0.05f), Quaternion.Identity);
            history.RecordPose(framePose, i == 0 ? 0.0f : 1.0f / 60.0f);
        }

        var context = new SomaPoseTransform[SomaModelContract.ContextPoseCount * SomaModelContract.JointCount];
        history.TrySampleContextToken(context).ShouldBeTrue();

        // Newest frame is at z = 0.55; 30 Hz steps back give z = 0.25, 0.35, 0.45, 0.55.
        context[3 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.55f, 1.0e-4f);
        context[2 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.45f, 1.0e-4f);
        context[1 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.35f, 1.0e-4f);
        context[0 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.25f, 1.0e-4f);

        // Physics root clamp shifts all recorded context roots identically.
        history.ApplyRootTranslationOffset(new Vector3(0.0f, 0.0f, -0.20f));
        history.TrySampleContextToken(context).ShouldBeTrue();
        context[3 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.35f, 1.0e-4f);
        context[0 * SomaModelContract.JointCount].Position.Z.ShouldBe(0.05f, 1.0e-4f);
    }

    [Test]
    public void AsyncLocomotionDriver_BackgroundWorkerReplansSmoothlyAcrossSteeringChanges()
    {
        var session = new GuidedSyntheticSomaSession(simulatedInferenceDelayMs: 4);
        using var driver = new MotionBricksAsyncLocomotionDriver(
            session,
            ownsSession: false,
            useBackgroundThread: true);

        var evaluated = new MotionBricksEvaluatedPose();
        var stopwatch = Stopwatch.StartNew();

        // Walk forward (+Z) for ~0.75 s so acceleration and background replans build steady stride.
        for (int i = 0; i < 45; i++)
        {
            driver.AdvanceAndEvaluate(CreateSteering(new Vector2(0.0f, 0.6f), dt: 1.0f / 60.0f), evaluated);
            Thread.Sleep(4);
        }

        evaluated.HasValidPose.ShouldBeTrue();
        driver.CompletedReplanCount.ShouldBeGreaterThanOrEqualTo(1);
        float zAfterForward = evaluated.CurrentBodyPosition.Z;
        zAfterForward.ShouldBeGreaterThan(0.15f);

        // Turn right (+X) and run for ~0.75 s.
        for (int i = 0; i < 45; i++)
        {
            driver.AdvanceAndEvaluate(CreateSteering(new Vector2(1.0f, 0.0f), dt: 1.0f / 60.0f), evaluated);
            Thread.Sleep(4);
        }

        driver.CompletedReplanCount.ShouldBeGreaterThanOrEqualTo(2);
        evaluated.CurrentBodyPosition.X.ShouldBeGreaterThan(0.15f);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(5000);
    }

    [Test]
    public void AsyncLocomotionDriver_InsideAnimStateMachineComponent_DrivesJoystickWalkTurnAndStop()
    {
        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid);
        var stateMachineComp = root.AddComponent<AnimStateMachineComponent>()!;
        stateMachineComp.Humanoid = humanoid;
        stateMachineComp.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer;

        var session = new GuidedSyntheticSomaSession(simulatedInferenceDelayMs: 0);
        using var driver = new MotionBricksAsyncLocomotionDriver(
            session,
            ownsSession: false,
            useBackgroundThread: false);

        var motion = new MotionBricksLocomotionMotion
        {
            Name = "SmartLocomotion",
            RuntimeDriverOverride = driver,
        };
        var state = new AnimState(motion, "Locomotion");
        stateMachineComp.StateMachine.Layers.Add(new AnimLayer(state) { InitialState = state });

        var moveX = new AnimFloat("MoveX", 0.0f);
        var moveY = new AnimFloat("MoveY", 1.0f);
        stateMachineComp.StateMachine.Variables["MoveX"] = moveX;
        stateMachineComp.StateMachine.Variables["MoveY"] = moveY;

        Vector3 totalDelta = Vector3.Zero;
        stateMachineComp.RootMotionEvaluated += (_, delta) => totalDelta += delta.Translation;

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(stateMachineComp, null)!;
        prepared.ShouldBeTrue(stateMachineComp.PlaybackCapabilityDiagnostic);

        long frame60 = Stopwatch.Frequency / 60;
        for (int i = 0; i < 45; i++)
        {
            EvaluateAndApplyMethod.Invoke(stateMachineComp, [frame60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(stateMachineComp, null);
        }

        totalDelta.Z.ShouldBeGreaterThan(0.25f);

        // Turn toward +X and continue stepping.
        moveX.FloatValue = 1.0f;
        moveY.FloatValue = 0.0f;
        Vector3 deltaDuringTurn = Vector3.Zero;
        stateMachineComp.RootMotionEvaluated += (_, delta) => deltaDuringTurn += delta.Translation;

        for (int i = 0; i < 45; i++)
        {
            EvaluateAndApplyMethod.Invoke(stateMachineComp, [frame60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(stateMachineComp, null);
        }

        deltaDuringTurn.X.ShouldBeGreaterThan(0.25f);
        driver.CompletedReplanCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    private static MotionBricksSteeringInput CreateSteering(
        Vector2 moveInput,
        float dt,
        bool strafeMode = false,
        float desiredFacingYaw = 0.0f)
        => new(
            MoveInput: moveInput,
            DesiredFacingYawRadians: desiredFacingYaw,
            StrafeMode: strafeMode,
            StyleIndex: 0,
            IsGrounded: true,
            WalkSpeedMetersPerSecond: 1.5f,
            RunSpeedMetersPerSecond: 4.2f,
            ReplanIntervalSeconds: 4.0f / 30.0f,
            InputReplanThreshold: 0.25f,
            DeltaSeconds: dt);

    private static SceneNode CreateTestHumanoidRig(out HumanoidComponent humanoid)
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(translation: new(0.0f, 1.0f, 0.0f)));
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

    /// <summary>
    /// Synthetic SOMA inference session that interpolates from context slot 3 to target slot 4
    /// in aligned model space so unit tests exercise the full Smart Locomotion, encoding, async
    /// worker, decoding, and endpoint-correction loop.
    /// </summary>
    private sealed class GuidedSyntheticSomaSession(int simulatedInferenceDelayMs) : IMotionBricksInferenceSession
    {
        public bool IsEngineReady => true;

        public bool TryBuildOrLoadEngine(out bool loadedFromCache, out string? error)
        {
            loadedFromCache = true;
            error = null;
            return true;
        }

        public bool TryRunSomaInference(SomaInferenceInputBuffer input, SomaInferenceOutputBuffer output, out string? error)
        {
            if (simulatedInferenceDelayMs > 0)
                Thread.Sleep(simulatedInferenceDelayMs);

            Array.Clear(output.PredGlobalMotions);

            // Pick the shortest allowed token count so urgent replans visibly produce shorter sequences.
            int chosenTokens = 8;
            for (int i = 0; i < SomaModelContract.AllowedTokenSlotCount; i++)
            {
                if (input.AllowedPredNumTokens[i] != 0)
                {
                    chosenTokens = SomaModelContract.MinTokenCount + i;
                    break;
                }
            }
            output.PredNumTokens[0] = chosenTokens;

            int validFrames = SomaModelContract.ValidFrameCount(chosenTokens);
            int rotOffset = SomaModelContract.GlobalRootFeatureCount + SomaModelContract.LocalPosePositionFeatureCount;
            int contactOffset = SomaModelContract.GlobalMotionFeatureCount - SomaModelContract.FootContactCount;

            int startRootBase = 3 * SomaModelContract.GlobalRootFeatureCount;
            int endRootBase = 4 * SomaModelContract.GlobalRootFeatureCount;
            var startPos = new Vector3(
                input.GlobalRootValues[startRootBase + 0],
                input.GlobalRootValues[startRootBase + 1],
                input.GlobalRootValues[startRootBase + 2]);
            var endPos = new Vector3(
                input.GlobalRootValues[endRootBase + 0],
                input.GlobalRootValues[endRootBase + 1],
                input.GlobalRootValues[endRootBase + 2]);

            int localPoseRotOffset = SomaModelContract.LocalPosePositionFeatureCount;
            int startPoseBase = 3 * SomaModelContract.LocalPoseFeatureCount + localPoseRotOffset;
            int endPoseBase = 4 * SomaModelContract.LocalPoseFeatureCount + localPoseRotOffset;

            for (int frame = 0; frame < validFrames; frame++)
            {
                float t = validFrames > 1 ? frame / (float)(validFrames - 1) : 1.0f;
                int rawFrame = SomaModelContract.BoundaryFrameCount + frame;
                int rawBase = rawFrame * SomaModelContract.GlobalMotionFeatureCount;

                Vector3 rootPos = Vector3.Lerp(startPos, endPos, t);
                output.PredGlobalMotions[rawBase + 0] = rootPos.X;
                output.PredGlobalMotions[rawBase + 1] = rootPos.Y;
                output.PredGlobalMotions[rawBase + 2] = rootPos.Z;

                for (int joint = 0; joint < SomaModelContract.JointCount; joint++)
                {
                    Quaternion q0 = Read6D(input.LocalPoses, startPoseBase + joint * 6);
                    Quaternion q1 = Read6D(input.LocalPoses, endPoseBase + joint * 6);
                    Quaternion q = Quaternion.Slerp(q0, q1, t);
                    SomaTensorCodec.RotationTo6D(q, out Vector3 xAxis, out Vector3 yAxis);

                    int dst = rawBase + rotOffset + joint * 6;
                    output.PredGlobalMotions[dst + 0] = xAxis.X;
                    output.PredGlobalMotions[dst + 1] = xAxis.Y;
                    output.PredGlobalMotions[dst + 2] = xAxis.Z;
                    output.PredGlobalMotions[dst + 3] = yAxis.X;
                    output.PredGlobalMotions[dst + 4] = yAxis.Y;
                    output.PredGlobalMotions[dst + 5] = yAxis.Z;
                }

                output.PredGlobalMotions[rawBase + contactOffset + 0] = (frame % 8 < 4) ? 1.0f : 0.0f;
                output.PredGlobalMotions[rawBase + contactOffset + 2] = (frame % 8 >= 4) ? 1.0f : 0.0f;
            }

            error = null;
            return true;
        }

        private static Quaternion Read6D(float[] buffer, int offset)
            => SomaTensorCodec.RotationFrom6D(
                new Vector3(buffer[offset + 0], buffer[offset + 1], buffer[offset + 2]),
                new Vector3(buffer[offset + 3], buffer[offset + 4], buffer[offset + 5]));

        public void Dispose() { }
    }
}
