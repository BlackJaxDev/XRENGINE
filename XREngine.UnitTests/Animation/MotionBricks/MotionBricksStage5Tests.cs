using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.MotionBricks;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement.Modules;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.MotionBricks;

[TestFixture]
public sealed class MotionBricksStage5Tests
{
    private static readonly MethodInfo TryPreparePlaybackMethod =
        typeof(AnimStateMachineComponent).GetMethod("TryPrepareStateMachinePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo EvaluateAndApplyMethod =
        typeof(AnimStateMachineComponent).GetMethod("EvaluateAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo PublishRootMotionMethod =
        typeof(AnimStateMachineComponent).GetMethod("PublishRootMotion", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ApplyMusclePoseMethod =
        typeof(HumanoidComponent).GetMethod("ApplyCurrentMusclePose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void HumanoidToSomaPoseSampler_RoundTripsCanonicalMusclesAndBodyTransformExactly()
    {
        var calibration = SomaRestSkeletonCalibration.CanonicalDefault;
        var toMuscles = new SomaToHumanoidMuscleConverter(calibration);
        var toSoma = new HumanoidToSomaPoseSampler(calibration);

        var originalPose = new SomaPoseTransform[SomaModelContract.JointCount];
        calibration.ReferenceLocalPoses.CopyTo(originalPose, 0);

        // Author a rich non-neutral pose across root, spine, neck, head, both arms, and both legs.
        originalPose[0] = new SomaPoseTransform(
            new Vector3(1.25f, 0.96f, -0.80f),
            Quaternion.CreateFromYawPitchRoll(0.65f, 0.08f, -0.05f));
        originalPose[1] = new SomaPoseTransform(originalPose[1].Position, Quaternion.CreateFromYawPitchRoll(0.12f, 0.18f, -0.09f));
        originalPose[2] = new SomaPoseTransform(originalPose[2].Position, Quaternion.CreateFromYawPitchRoll(-0.10f, 0.14f, 0.07f));
        originalPose[3] = new SomaPoseTransform(originalPose[3].Position, Quaternion.CreateFromYawPitchRoll(0.08f, -0.11f, 0.06f));
        originalPose[4] = new SomaPoseTransform(originalPose[4].Position, Quaternion.CreateFromYawPitchRoll(0.15f, 0.10f, -0.05f));
        originalPose[6] = new SomaPoseTransform(originalPose[6].Position, Quaternion.CreateFromYawPitchRoll(-0.18f, -0.12f, 0.08f));

        originalPose[11] = new SomaPoseTransform(originalPose[11].Position, Quaternion.CreateFromYawPitchRoll(0.35f, -0.25f, 0.40f));
        originalPose[12] = new SomaPoseTransform(originalPose[12].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.55f));
        originalPose[17] = new SomaPoseTransform(originalPose[17].Position, Quaternion.CreateFromYawPitchRoll(-0.30f, 0.20f, -0.45f));
        originalPose[18] = new SomaPoseTransform(originalPose[18].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.60f));

        originalPose[22] = new SomaPoseTransform(originalPose[22].Position, Quaternion.CreateFromYawPitchRoll(0.10f, 0.45f, -0.12f));
        originalPose[23] = new SomaPoseTransform(originalPose[23].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.55f));
        originalPose[24] = new SomaPoseTransform(originalPose[24].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f));
        originalPose[25] = new SomaPoseTransform(originalPose[25].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.20f));

        originalPose[26] = new SomaPoseTransform(originalPose[26].Position, Quaternion.CreateFromYawPitchRoll(-0.12f, -0.35f, 0.15f));
        originalPose[27] = new SomaPoseTransform(originalPose[27].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.40f));
        originalPose[28] = new SomaPoseTransform(originalPose[28].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.20f));
        originalPose[29] = new SomaPoseTransform(originalPose[29].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.15f));

        const float avatarLegScale = 1.15f;
        var originalMuscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        toMuscles.ConvertFrame(originalPose, avatarLegScale, originalMuscles, out Vector3 bodyPos, out Quaternion bodyRot);

        var reconstructedPose = new SomaPoseTransform[SomaModelContract.JointCount];
        toSoma.SampleSomaPose(bodyPos, bodyRot, originalMuscles, avatarLegScale, reconstructedPose);

        var roundTrippedMuscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
        toMuscles.ConvertFrame(reconstructedPose, avatarLegScale, roundTrippedMuscles, out Vector3 roundPos, out Quaternion roundRot);

        Vector3.Distance(bodyPos, roundPos).ShouldBeLessThan(1.0e-4f);
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(bodyRot), Quaternion.Normalize(roundRot))).ShouldBeGreaterThan(0.9999f);

        foreach (EHumanoidValue channel in MotionBricksLocomotionMotion.DrivenMuscleChannels)
        {
            int idx = (int)channel;
            MathF.Abs(originalMuscles[idx] - roundTrippedMuscles[idx])
                .ShouldBeLessThan(1.5e-3f, $"Muscle {channel} diverged during SOMA->Humanoid->SOMA round-trip.");
        }
    }

    [Test]
    public void AnimStateMachineComponent_ClipToMotionBricksTransition_SeedsContextHistoryFromLiveClipPose()
    {
        SceneNode root = CreateTestHumanoidRig(
            out HumanoidComponent humanoid,
            rootTranslation: new Vector3(2.0f, 0.0f, 3.0f),
            rootRotation: Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));

        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;
        animator.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer;

        // Traditional clip holds a distinct non-neutral pose (bent leg, raised arm, forward spine).
        AnimationClip landClip = CreateMultiMuscleClip(
            (EHumanoidValue.LeftUpperLegFrontBack, 0.65f),
            (EHumanoidValue.RightArmDownUp, -0.45f),
            (EHumanoidValue.SpineFrontBack, 0.30f));

        var capturingSession = new CapturingSyntheticSomaSession();
        using var driver = new MotionBricksAsyncLocomotionDriver(
            capturingSession,
            ownsSession: false,
            useBackgroundThread: false);
        var locomotionMotion = new MotionBricksLocomotionMotion
        {
            Name = "Locomotion",
            RuntimeDriverOverride = driver,
        };

        var landState = new AnimState(landClip, "Land");
        var locomotionState = new AnimState(locomotionMotion, "Locomotion");
        landState.Transitions.Add(new AnimStateTransition
        {
            DestinationState = locomotionState,
            HasExitTime = false,
            BlendDuration = 0.0f,
            Conditions = [new AnimTransitionCondition("IsGrounded", isThisState: true)],
        });

        var layer = new AnimLayer(landState, locomotionState) { InitialState = landState };
        animator.StateMachine.Layers.Add(layer);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", false);
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.5f);

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        long frame60Ticks = Stopwatch.Frequency / 60;

        // Run 10 frames in LandClip so the live humanoid pose history fills with the clip's pose.
        for (int i = 0; i < 10; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [frame60Ticks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
        }

        animator.StateMachine.GetLiveHumanoidPoseHistory().ShouldNotBeNull();
        animator.StateMachine.GetLiveHumanoidPoseHistory()!.SampleCount.ShouldBeGreaterThanOrEqualTo(4);
        driver.ContextSeedCount.ShouldBe(0L);

        // Transition into MotionBricks locomotion.
        animator.SetBool("IsGrounded", true);
        for (int i = 0; i < 4; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [frame60Ticks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
        }

        layer.CurrentState.ShouldBe(locomotionState);
        driver.ContextSeedCount.ShouldBeGreaterThanOrEqualTo(1L);
        capturingSession.CapturedFirstInput.ShouldBeTrue();

        // Verify the first SOMA inference received the live clip's non-neutral pose in context slot 3 (not neutral rest).
        // LeftLeg is joint 22, Spine1 is joint 1, RightArm is joint 17.
        float spineRotationDeviation = MeasureContextLocalRotationDeviationFromIdentity(
            capturingSession.FirstContextLocalPoses, frameIndex: 3, jointIndex: 1);
        float leftLegRotationDeviation = MeasureContextLocalRotationDeviationFromIdentity(
            capturingSession.FirstContextLocalPoses, frameIndex: 3, jointIndex: 22);
        float rightArmRotationDeviation = MeasureContextLocalRotationDeviationFromIdentity(
            capturingSession.FirstContextLocalPoses, frameIndex: 3, jointIndex: 17);

        spineRotationDeviation.ShouldBeGreaterThan(0.01f);
        leftLegRotationDeviation.ShouldBeGreaterThan(0.05f);
        rightArmRotationDeviation.ShouldBeGreaterThan(0.05f);
    }

    [Test]
    public void AnimStateMachineComponent_MotionBricksBaseLayer_ComposesWithUpperBodyOverrideAndAdditiveLayers()
    {
        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid);
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;
        animator.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer;

        // Layer 0 (Base Override): MotionBricks driving known walking leg + spine + arm muscles + forward root motion.
        var baseDriver = new DeterministicWalkTestDriver();
        var locomotionMotion = new MotionBricksLocomotionMotion
        {
            Name = "Locomotion",
            RuntimeDriverOverride = baseDriver,
        };
        var baseState = new AnimState(locomotionMotion, "Locomotion");
        var baseLayer = new AnimLayer(baseState)
        {
            InitialState = baseState,
            ApplyType = AnimLayer.EApplyType.Override,
            Weight = 1.0f,
        };

        // Layer 1 (Upper-body Override): overwrites ONLY RightArmDownUp and RightForearmStretch.
        AnimationClip upperWaveClip = CreateMultiMuscleClip(
            (EHumanoidValue.RightArmDownUp, 0.85f),
            (EHumanoidValue.RightForearmStretch, 0.70f));
        var waveState = new AnimState(upperWaveClip, "UpperBodyWave");
        var upperOverrideLayer = new AnimLayer(waveState)
        {
            InitialState = waveState,
            ApplyType = AnimLayer.EApplyType.Override,
            Weight = 1.0f,
        };

        // Layer 2 (Additive): reference pose at t=0 is 0.0f, then holds +0.25f on SpineFrontBack
        // so additive evaluation computes sample(t) - sample(0) = +0.25f on top of Layer 0.
        AnimationClip spineLeanClip = CreateAdditiveMuscleClip(EHumanoidValue.SpineFrontBack, 0.25f);
        var leanState = new AnimState(spineLeanClip, "AdditiveSpineLean");
        var additiveLayer = new AnimLayer(leanState)
        {
            InitialState = leanState,
            ApplyType = AnimLayer.EApplyType.Additive,
            Weight = 1.0f,
        };

        animator.StateMachine.Layers.Add(baseLayer);
        animator.StateMachine.Layers.Add(upperOverrideLayer);
        animator.StateMachine.Layers.Add(additiveLayer);

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        Vector3 accumulatedRootDelta = Vector3.Zero;
        animator.RootMotionEvaluated += (_, delta) => accumulatedRootDelta += delta.Translation;

        long frame60Ticks = Stopwatch.Frequency / 60;
        for (int i = 0; i < 6; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [frame60Ticks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(animator, null);
        }

        // Layer 1 overrode right arm muscles.
        humanoid.TryGetMuscleValue(EHumanoidValue.RightArmDownUp, out float rightArm).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.RightForearmStretch, out float rightForearm).ShouldBeTrue();
        rightArm.ShouldBe(0.85f, 1.0e-3f);
        rightForearm.ShouldBe(0.70f, 1.0e-3f);

        // Layer 0 legs remain untouched by upper-body override and additive spine layers.
        humanoid.TryGetMuscleValue(EHumanoidValue.LeftUpperLegFrontBack, out float leftLeg).ShouldBeTrue();
        leftLeg.ShouldBe(DeterministicWalkTestDriver.ExpectedLeftLegMuscle, 1.0e-3f);

        // Layer 2 added +0.25f onto Layer 0's base SpineFrontBack (+0.15f => +0.40f).
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float spine).ShouldBeTrue();
        spine.ShouldBe(DeterministicWalkTestDriver.ExpectedBaseSpineMuscle + 0.25f, 1.0e-3f);

        // Layer 0 root motion still publishes cleanly alongside upper-body/additive layers.
        accumulatedRootDelta.Length().ShouldBeGreaterThan(0.05f);
    }

    [Test]
    public void HotPathAllocationAudit_SteadyStateMotionBricksAndRootMotionLoopAllocatesZeroBytes()
    {
        var calibration = SomaRestSkeletonCalibration.CanonicalDefault;
        var session = new CapturingSyntheticSomaSession();
        using var driver = new MotionBricksAsyncLocomotionDriver(
            session,
            ownsSession: false,
            useBackgroundThread: false,
            calibration: calibration);

        var evaluatedPose = new MotionBricksEvaluatedPose();
        var historyBuffer = new HumanoidPoseHistoryBuffer();
        HumanoidPoseHistoryFrame[] seedScratch =
        [
            new HumanoidPoseHistoryFrame(),
            new HumanoidPoseHistoryFrame(),
            new HumanoidPoseHistoryFrame(),
            new HumanoidPoseHistoryFrame(),
        ];
        var rootMotionModule = new RootMotionMovementModule("ZeroAllocAudit");

        const float dt = 1.0f / 60.0f;

        // Warm up JIT and lazy caches before measuring allocations.
        for (int i = 0; i < 20; i++)
            RunHotPathIteration(driver, evaluatedPose, historyBuffer, seedScratch, rootMotionModule, dt, i);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++)
            RunHotPathIteration(driver, evaluatedPose, historyBuffer, seedScratch, rootMotionModule, dt, i);
        long afterBytes = GC.GetAllocatedBytesForCurrentThread();

        long allocatedBytes = afterBytes - beforeBytes;
        allocatedBytes.ShouldBe(0L, $"MotionBricks hot path allocated {allocatedBytes} bytes over 120 frames.");
    }

    [Test]
    [Explicit("Runs live NVIDIA TensorRT SOMA inference on the local CUDA GPU with staged ONNX weights.")]
    public void LiveTensorRtGpu_BuildsCachedSomaPlanAndDrivesMotionBricksInference()
    {
        bool available = MotionBricksTensorRtBackend.Instance.IsAvailable(out string? availError);
        available.ShouldBeTrue(availError ?? "TensorRT native bridge is unavailable.");

        int trtVersion = MotionBricksTensorRtBridge.GetTensorRtVersion();
        trtVersion.ShouldBeGreaterThanOrEqualTo(100000);

        string onnxPath = FindRepoRelativeFile(Path.Combine("Build", "Dependencies", "MotionBricks", "soma", "mmm_v6_soma_fp16.onnx"));
        File.Exists(onnxPath).ShouldBeTrue($"Missing staged SOMA weights at {onnxPath}");
        string cacheDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(onnxPath)!)!, "cache");

        var config = new MotionBricksSessionConfig
        {
            OnnxModelPath = onnxPath,
            PlanCacheDirectory = cacheDir,
            EnableFp16 = true,
            EnableTf32 = true,
            BuilderOptimizationLevel = 3,
        };

        MotionBricksTensorRtBackend.Instance.TryCreateSession(config, out IMotionBricksInferenceSession? session, out string? createError)
            .ShouldBeTrue(createError ?? "Failed to create TensorRT session.");
        session.ShouldNotBeNull();

        using (session)
        {
            var buildSw = Stopwatch.StartNew();
            session!.TryBuildOrLoadEngine(out bool loadedFromCache, out string? buildError)
                .ShouldBeTrue(buildError ?? "Failed to build or load SOMA TensorRT plan.");
            buildSw.Stop();

            TestContext.Progress.WriteLine(
                $"[LiveTensorRtGpu] TensorRT={trtVersion}, LoadedFromCache={loadedFromCache}, BuildOrLoadMs={buildSw.Elapsed.TotalMilliseconds:F1}");

            using var driver = new MotionBricksAsyncLocomotionDriver(
                session,
                ownsSession: false,
                useBackgroundThread: false);

            var evaluatedPose = new MotionBricksEvaluatedPose();
            const float dt = 1.0f / 60.0f;
            float maxLegMuscle = 0.0f;
            double totalInferenceMs = 0.0;
            int replanSamples = 0;

            for (int frame = 0; frame < 60; frame++)
            {
                long prevReplans = driver.CompletedReplanCount;
                var steering = new MotionBricksSteeringInput(
                    MoveInput: new Vector2(0.0f, 0.85f),
                    DesiredFacingYawRadians: 0.0f,
                    StrafeMode: false,
                    StyleIndex: 0,
                    IsGrounded: true,
                    WalkSpeedMetersPerSecond: 1.5f,
                    RunSpeedMetersPerSecond: 4.2f,
                    ReplanIntervalSeconds: 4.0f / 30.0f,
                    InputReplanThreshold: 0.15f,
                    DeltaSeconds: dt);

                driver.AdvanceAndEvaluate(steering, evaluatedPose);
                evaluatedPose.HasValidPose.ShouldBeTrue(driver.DiagnosticStatus);

                maxLegMuscle = MathF.Max(
                    maxLegMuscle,
                    MathF.Max(
                        MathF.Abs(evaluatedPose.MuscleValues[(int)EHumanoidValue.LeftUpperLegFrontBack]),
                        MathF.Abs(evaluatedPose.MuscleValues[(int)EHumanoidValue.RightUpperLegFrontBack])));

                if (driver.CompletedReplanCount > prevReplans)
                {
                    totalInferenceMs += driver.LastInferenceMilliseconds;
                    replanSamples++;
                }
            }

            double avgInferenceMs = replanSamples > 0 ? totalInferenceMs / replanSamples : driver.LastInferenceMilliseconds;
            Vector3 displacement = evaluatedPose.CurrentBodyPosition - evaluatedPose.CanonicalBodyPosition;
            TestContext.Progress.WriteLine(
                $"[LiveTensorRtGpu] Replans={driver.CompletedReplanCount}, AvgInferenceMs={avgInferenceMs:F2}, LastDecodedFrames={driver.LastDecodedFrameCount}, BodyDisplacement=({displacement.X:F3}, {displacement.Y:F3}, {displacement.Z:F3}), MaxLegMuscle={maxLegMuscle:F3}");

            driver.CompletedReplanCount.ShouldBeGreaterThanOrEqualTo(2L);
            driver.LastDecodedFrameCount.ShouldBeGreaterThanOrEqualTo(
                SomaModelContract.ValidFrameCount(SomaModelContract.MinTokenCount));
            float.IsFinite(evaluatedPose.CurrentBodyPosition.X).ShouldBeTrue();
            float.IsFinite(evaluatedPose.CurrentBodyPosition.Y).ShouldBeTrue();
            float.IsFinite(evaluatedPose.CurrentBodyPosition.Z).ShouldBeTrue();
            maxLegMuscle.ShouldBeGreaterThan(0.05f);
        }
    }

    private static string FindRepoRelativeFile(string relativePath)
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.GetFullPath(relativePath);
    }

    private static void RunHotPathIteration(
        MotionBricksAsyncLocomotionDriver driver,
        MotionBricksEvaluatedPose evaluatedPose,
        HumanoidPoseHistoryBuffer historyBuffer,
        HumanoidPoseHistoryFrame[] seedScratch,
        RootMotionMovementModule rootMotionModule,
        float dt,
        int frameIndex)
    {
        float angle = frameIndex * 0.03f;
        var steering = new MotionBricksSteeringInput(
            MoveInput: new Vector2(MathF.Sin(angle) * 0.7f, MathF.Cos(angle) * 0.7f),
            DesiredFacingYawRadians: angle,
            StrafeMode: (frameIndex & 32) != 0,
            StyleIndex: 0,
            IsGrounded: true,
            WalkSpeedMetersPerSecond: 1.5f,
            RunSpeedMetersPerSecond: 4.2f,
            ReplanIntervalSeconds: 4.0f / 30.0f,
            InputReplanThreshold: 0.15f,
            DeltaSeconds: dt);

        driver.AdvanceAndEvaluate(steering, evaluatedPose);
        historyBuffer.RecordFrame(
            evaluatedPose.CurrentBodyPosition,
            evaluatedPose.CurrentBodyRotation,
            evaluatedPose.MuscleValues,
            dt);

        if (frameIndex % 30 == 0 && historyBuffer.TrySampleFourContextFrames(seedScratch))
            driver.SeedContextFromHumanoidHistory(seedScratch);

        rootMotionModule.AccumulateRootMotionDelta(new Vector3(0.0f, 0.0f, 0.03f), Quaternion.Identity);
        var moveCtx = new MovementModule.MovementContext(
            Vector3.Zero,
            Vector3.Zero,
            4.2f,
            10.0f,
            dt,
            true,
            false,
            Vector3.UnitY,
            new Vector3(0.0f, -9.81f, 0.0f));
        _ = rootMotionModule.ProcessGroundMovement(in moveCtx);

        driver.ApplyPhysicsRootReconciliation(
            new Vector3(0.0f, 0.0f, frameIndex * 0.02f),
            new Vector3(0.0f, 0.0f, 1.2f),
            frameIndex % 20 == 0 ? -Vector3.UnitZ : Vector3.Zero,
            isBlocked: frameIndex % 20 == 0);
    }

    private static float MeasureContextLocalRotationDeviationFromIdentity(
        float[] localPosesFlat,
        int frameIndex,
        int jointIndex)
    {
        int offset = frameIndex * SomaModelContract.LocalPoseFeatureCount
            + SomaModelContract.LocalPosePositionFeatureCount
            + jointIndex * 6;
        Quaternion q = SomaTensorCodec.RotationFrom6D(
            new Vector3(localPosesFlat[offset + 0], localPosesFlat[offset + 1], localPosesFlat[offset + 2]),
            new Vector3(localPosesFlat[offset + 3], localPosesFlat[offset + 4], localPosesFlat[offset + 5]));
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(q), Quaternion.Identity));
        return 1.0f - Math.Clamp(dot, 0.0f, 1.0f);
    }

    private static AnimationClip CreateMultiMuscleClip(params (EHumanoidValue Muscle, float Value)[] channels)
    {
        var clip = new AnimationClip
        {
            Name = "TestClip",
            LengthInSeconds = 1.0f,
            Looped = true,
        };

        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var getHumanoid = new AnimationMember("GetComponentInHierarchy", EAnimationMemberType.Method)
        {
            MethodArguments = ["HumanoidComponent"],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        sceneNode.Children.Add(getHumanoid);

        foreach ((EHumanoidValue muscle, float value) in channels)
        {
            var anim = new PropAnimFloat
            {
                LengthInSeconds = 1.0f,
                Looped = true,
                DefaultValue = value,
            };
            anim.Keyframes.Add(new FloatKeyframe(0.0f, value, 0.0f, EVectorInterpType.Linear));
            anim.Keyframes.Add(new FloatKeyframe(1.0f, value, 0.0f, EVectorInterpType.Linear));

            var setter = new AnimationMember("SetImportedRawValue", EAnimationMemberType.Method)
            {
                MethodArguments = [muscle, 0.0f, false],
                AnimatedMethodArgumentIndex = 1,
                CacheReturnValue = false,
                DefaultValue = 0.0f,
                Animation = anim,
            };
            getHumanoid.Children.Add(setter);
        }

        clip.RootMember ??= new AnimationMember(string.Empty, EAnimationMemberType.Group);
        clip.RootMember.Children.Add(sceneNode);
        return clip;
    }

    private static AnimationClip CreateAdditiveMuscleClip(EHumanoidValue muscle, float deltaValue)
    {
        var clip = new AnimationClip
        {
            Name = "AdditiveTestClip",
            LengthInSeconds = 1.0f,
            Looped = true,
        };

        var anim = new PropAnimFloat
        {
            LengthInSeconds = 1.0f,
            Looped = true,
            DefaultValue = 0.0f,
        };
        anim.Keyframes.Add(new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear));
        anim.Keyframes.Add(new FloatKeyframe(0.01f, deltaValue, 0.0f, EVectorInterpType.Linear));
        anim.Keyframes.Add(new FloatKeyframe(1.0f, deltaValue, 0.0f, EVectorInterpType.Linear));

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

    private static SceneNode CreateTestHumanoidRig(
        out HumanoidComponent humanoid,
        Vector3? rootTranslation = null,
        Quaternion? rootRotation = null)
    {
        var root = new SceneNode(
            "Root",
            new Transform(
                translation: rootTranslation ?? Vector3.Zero,
                rotation: rootRotation ?? Quaternion.Identity));
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

    private sealed class CapturingSyntheticSomaSession : IMotionBricksInferenceSession
    {
        public bool IsEngineReady => true;
        public bool CapturedFirstInput { get; private set; }
        public float[] FirstContextLocalPoses { get; } =
            new float[(SomaModelContract.ContextPoseCount + SomaModelContract.TargetPoseCapacity) * SomaModelContract.LocalPoseFeatureCount];

        public bool TryBuildOrLoadEngine(out bool loadedFromCache, out string? error)
        {
            loadedFromCache = true;
            error = null;
            return true;
        }

        public bool TryRunSomaInference(SomaInferenceInputBuffer input, SomaInferenceOutputBuffer output, out string? error)
        {
            if (!CapturedFirstInput)
            {
                input.LocalPoses.CopyTo(FirstContextLocalPoses, 0);
                CapturedFirstInput = true;
            }

            Array.Clear(output.PredGlobalMotions);
            int chosenTokens = SomaModelContract.MinTokenCount;
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

                output.PredGlobalMotions[rawBase + contactOffset + 0] = 1.0f;
                output.PredGlobalMotions[rawBase + contactOffset + 2] = 1.0f;
            }

            error = null;
            return true;
        }

        public void Dispose()
        {
        }

        private static Quaternion Read6D(float[] buffer, int offset)
            => SomaTensorCodec.RotationFrom6D(
                new Vector3(buffer[offset + 0], buffer[offset + 1], buffer[offset + 2]),
                new Vector3(buffer[offset + 3], buffer[offset + 4], buffer[offset + 5]));
    }

    private sealed class DeterministicWalkTestDriver : IMotionBricksLocomotionDriver
    {
        public const float ExpectedLeftLegMuscle = 0.55f;
        public const float ExpectedBaseSpineMuscle = 0.15f;
        private float _distanceZ;

        public bool IsReady => true;
        public string DiagnosticStatus => "Ready (Deterministic test driver).";

        public void OnStateEntered()
        {
        }

        public void OnStateExited()
        {
        }

        public void ResetPlayback()
            => _distanceZ = 0.0f;

        public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination)
        {
            _distanceZ += 1.5f * input.DeltaSeconds;
            Array.Clear(destination.MuscleValues);
            destination.MuscleValues[(int)EHumanoidValue.LeftUpperLegFrontBack] = ExpectedLeftLegMuscle;
            destination.MuscleValues[(int)EHumanoidValue.SpineFrontBack] = ExpectedBaseSpineMuscle;
            destination.MuscleValues[(int)EHumanoidValue.RightArmDownUp] = -0.30f;
            destination.MuscleValues[(int)EHumanoidValue.RightForearmStretch] = -0.20f;
            destination.CanonicalBodyPosition = new Vector3(0.0f, 1.0f, 0.0f);
            destination.CanonicalBodyRotation = Quaternion.Identity;
            destination.CurrentBodyPosition = new Vector3(0.0f, 1.0f, _distanceZ);
            destination.CurrentBodyRotation = Quaternion.Identity;
            destination.HasValidPose = true;
        }

        public void Dispose()
        {
        }
    }
}
