using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.MotionBricks;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Movement.Modules;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.MotionBricks;

[TestFixture]
[NonParallelizable]
public sealed class MotionBricksStage4Tests
{
    private const float FixedDelta = 1.0f / 60.0f;

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

    private JoltScene? _joltScene;

    [SetUp]
    public void SetUp()
    {
        _joltScene = new JoltScene();
        _joltScene.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _joltScene?.Destroy();
        _joltScene = null;
    }

    [Test]
    public void RootMotionMovementModule_ConvertsAccumulatedDeltasToVelocityAndAppliesAirGravity()
    {
        var module = new RootMotionMovementModule("TestRootMotion")
        {
            MaxSpeed = 10.0f,
            GravityScale = 1.0f,
        };

        // Accumulate two render frames of root motion before one 60 Hz physics step.
        module.AccumulateRootMotionDelta(new Vector3(0.0f, 0.2f, 0.03f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.05f));
        module.AccumulateRootMotionDelta(new Vector3(0.0f, -0.1f, 0.02f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.05f));

        var groundContext = new MovementModule.MovementContext(
            inputDirection: Vector3.Zero,
            currentVelocity: Vector3.Zero,
            targetSpeed: 4.0f,
            maxSpeed: 10.0f,
            deltaTime: FixedDelta,
            isGrounded: true,
            isCollidingUp: false,
            upDirection: Vector3.UnitY,
            gravity: new Vector3(0.0f, -9.81f, 0.0f));

        MovementModule.MovementResult groundResult = module.ProcessGroundMovement(in groundContext);
        // Planar delta Z = 0.05m over 1/60s => 3.0 m/s; Y delta stripped from horizontal ground motion.
        groundResult.NewVelocity.Z.ShouldBe(3.0f, 1.0e-3f);
        groundResult.NewVelocity.Y.ShouldBe(0.0f, 1.0e-5f);
        module.LastRequestedHorizontalDisplacement.Z.ShouldBe(0.05f, 1.0e-5f);

        Quaternion yawDelta = module.ConsumeAccumulatedYawDelta();
        Vector3 rotatedForward = Vector3.Transform(Vector3.UnitZ, yawDelta);
        MathF.Atan2(rotatedForward.X, rotatedForward.Z).ShouldBe(0.10f, 1.0e-4f);

        // Airborne step applies gravity and discards any stale grounded root deltas.
        module.AccumulateRootMotionDelta(new Vector3(5.0f, 0.0f, 5.0f), Quaternion.Identity);
        var airContext = new MovementModule.MovementContext(
            inputDirection: Vector3.UnitZ,
            currentVelocity: new Vector3(0.0f, 0.0f, 1.0f),
            targetSpeed: 4.0f,
            maxSpeed: 10.0f,
            deltaTime: FixedDelta,
            isGrounded: false,
            isCollidingUp: false,
            upDirection: Vector3.UnitY,
            gravity: new Vector3(0.0f, -9.81f, 0.0f));

        MovementModule.MovementResult airResult = module.ProcessAirMovement(in airContext);
        airResult.GravityApplied.ShouldBeTrue();
        airResult.NewVelocity.Y.ShouldBeLessThan(-0.1f);
        module.LastRequestedHorizontalDisplacement.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void CharacterPawnComponent_RootMotionSteering_PreservesAnalogStickMagnitudeAndCameraDirection()
    {
        var root = new SceneNode("Player", new RigidBodyTransform());
        var movement = root.AddComponent<CharacterMovement3DComponent>()!;
        var pawn = root.AddComponent<CharacterPawnComponent>()!;
        pawn.UseRootMotionSteering = true;

        // Camera facing +X (rotated +90 deg from +Z), right = -Z.
        Vector3 camForward = Vector3.UnitX;
        Vector3 camRight = -Vector3.UnitZ;

        // Push left thumbstick half-way forward (analog walk magnitude = 0.5).
        pawn.MoveForward(0.5f);
        pawn.MoveRight(0.0f);

        ThirdPersonSteeringSnapshot walkSnapshot = pawn.ComputeCameraRelativeSteering(camForward, camRight);
        walkSnapshot.DesiredWorldMoveXZ.X.ShouldBe(0.5f, 1.0e-4f);
        walkSnapshot.DesiredWorldMoveXZ.Y.ShouldBe(0.0f, 1.0e-4f);
        walkSnapshot.CameraYawRadians.ShouldBe(MathF.PI * 0.5f, 1.0e-4f);

        // Push stick fully diagonal; magnitude clamps to 1.0.
        pawn.MoveForward(1.0f);
        pawn.MoveRight(1.0f);
        ThirdPersonSteeringSnapshot runSnapshot = pawn.ComputeCameraRelativeSteering(camForward, camRight);
        runSnapshot.DesiredWorldMoveXZ.Length().ShouldBe(1.0f, 1.0e-4f);
    }

    [Test]
    public void JoltClosedLoop_MotionBricksRootMotionDrivesRealJoltCapsuleAndReconcilesWallCollision()
    {
        CreateFloor(halfExtents: new Vector3(20.0f, 0.5f, 20.0f), center: new Vector3(0.0f, -0.5f, 0.0f));
        // Solid wall blocking +Z travel at Z = 0.70m (box half-depth 0.1m => front face at Z = 0.60m).
        _joltScene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(new Vector3(5.0f, 2.0f, 0.1f)),
            (new Vector3(0.0f, 1.0f, 0.70f), Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();

        JoltCharacterVirtualController controller = CreateJoltController(new Vector3(0.0f, 0.90f, 0.0f));
        SettleControllerOnFloor(controller);

        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid);
        var movement = root.AddComponent<CharacterMovement3DComponent>()!;
        var rootMotionModule = new RootMotionMovementModule("JoltRootMotion");
        movement.MovementModule = rootMotionModule;

        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;

        var session = new GuidedSyntheticSomaSession();
        using var driver = new MotionBricksAsyncLocomotionDriver(session, ownsSession: false, useBackgroundThread: false);
        var motion = new MotionBricksLocomotionMotion { Name = "Locomotion", RuntimeDriverOverride = driver };
        var state = new AnimState(motion, "Locomotion");
        animator.StateMachine.Layers.Add(new AnimLayer(state) { InitialState = state });
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["StrafeMode"] = new AnimBool("StrafeMode", false);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", true);

        var bridge = root.AddComponent<ThirdPersonRootMotionBridgeComponent>()!;
        bridge.Animator = animator;
        bridge.CharacterMovement = movement;
        bridge.BindEndpoints();

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        // Establish initial foot position baseline.
        movement.PublishPhysicsStepFeedback(controller, FixedDelta);

        bool sawFreeForwardProgress = false;
        bool sawWallBlock = false;
        long frame60Ticks = Stopwatch.Frequency / 60;

        for (int step = 0; step < 90; step++)
        {
            bridge.ApplySteering(new ThirdPersonSteeringSnapshot(
                DesiredWorldMoveXZ: new Vector2(0.0f, 1.0f),
                CameraYawRadians: 0.0f,
                StrafeMode: false,
                SprintHeld: true,
                IsGrounded: controller.IsGrounded));

            EvaluateAndApplyMethod.Invoke(animator, [frame60Ticks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(animator, null);

            var moveCtx = new MovementModule.MovementContext(
                Vector3.Zero,
                Vector3.Zero,
                4.2f,
                10.0f,
                FixedDelta,
                controller.IsGrounded,
                controller.CollidingUp,
                Vector3.UnitY,
                new Vector3(0.0f, -9.81f, 0.0f));
            MovementModule.MovementResult groundResult = rootMotionModule.ProcessGroundMovement(in moveCtx);

            var desiredVelocity = new Vector3(groundResult.NewVelocity.X, -1.0f, groundResult.NewVelocity.Z);
            controller.SubmitMotion(new CharacterMotionCommand(
                desiredVelocity,
                CharacterMotionInputModel.Velocity,
                0.0f,
                FixedDelta));
            controller.ConsumeInputBuffer(FixedDelta);

            CharacterMovementPhysicsStepReport report = movement.PublishPhysicsStepFeedback(controller, FixedDelta);
            if (controller.FootPosition.Z > 0.15f)
                sawFreeForwardProgress = true;
            if (report.IsBlocked)
                sawWallBlock = true;
        }

        sawFreeForwardProgress.ShouldBeTrue();
        sawWallBlock.ShouldBeTrue();
        // Wall front face is at Z=0.60m and capsule radius is 0.25m, so capsule center must stay below ~0.42m.
        controller.FootPosition.Z.ShouldBeLessThan(0.45f);
        // Animation root was reconciled back to the physical capsule instead of drifting through the wall.
        MathF.Abs(motion.EvaluatedPose.CurrentBodyPosition.Z - controller.FootPosition.Z).ShouldBeLessThan(0.35f);
    }

    [Test]
    public void JoltClosedLoop_SteppingOffLedgeSetsIsGroundedFalseAndTransitionsToTraditionalFallClip()
    {
        // Short platform ending at Z = +0.35m so walking forward steps off into empty air.
        CreateFloor(halfExtents: new Vector3(4.0f, 0.5f, 0.45f), center: new Vector3(0.0f, -0.5f, -0.10f));

        JoltCharacterVirtualController controller = CreateJoltController(new Vector3(0.0f, 0.90f, 0.0f));
        SettleControllerOnFloor(controller);
        controller.IsGrounded.ShouldBeTrue();

        SceneNode root = CreateTestHumanoidRig(out HumanoidComponent humanoid);
        var movement = root.AddComponent<CharacterMovement3DComponent>()!;
        var rootMotionModule = new RootMotionMovementModule("JoltLedgeRootMotion");
        movement.MovementModule = rootMotionModule;

        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;

        var session = new GuidedSyntheticSomaSession();
        using var driver = new MotionBricksAsyncLocomotionDriver(session, ownsSession: false, useBackgroundThread: false);
        var locomotionMotion = new MotionBricksLocomotionMotion { Name = "Locomotion", RuntimeDriverOverride = driver };
        var fallClip = new AnimationClip { Name = "Fall", LengthInSeconds = 1.0f, Looped = true };

        var locomotionState = new AnimState(locomotionMotion, "Locomotion");
        var fallState = new AnimState(fallClip, "Fall");
        locomotionState.Transitions.Add(new AnimStateTransition
        {
            DestinationState = fallState,
            HasExitTime = false,
            BlendDuration = 0.05f,
            Conditions = [new AnimTransitionCondition("IsGrounded", isThisState: false)],
        });

        var layer = new AnimLayer(locomotionState, fallState) { InitialState = locomotionState };
        animator.StateMachine.Layers.Add(layer);
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 1.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["StrafeMode"] = new AnimBool("StrafeMode", false);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", true);

        var bridge = root.AddComponent<ThirdPersonRootMotionBridgeComponent>()!;
        bridge.Animator = animator;
        bridge.CharacterMovement = movement;
        bridge.BindEndpoints();

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        movement.PublishPhysicsStepFeedback(controller, FixedDelta);

        Vector3 velocity = Vector3.Zero;
        long frame60Ticks = Stopwatch.Frequency / 60;
        for (int step = 0; step < 90; step++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [frame60Ticks, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            PublishRootMotionMethod.Invoke(animator, null);

            var ctx = new MovementModule.MovementContext(
                Vector3.UnitZ,
                velocity,
                4.2f,
                10.0f,
                FixedDelta,
                controller.IsGrounded,
                controller.CollidingUp,
                Vector3.UnitY,
                new Vector3(0.0f, -9.81f, 0.0f));

            MovementModule.MovementResult stepResult = controller.IsGrounded
                ? rootMotionModule.ProcessGroundMovement(in ctx)
                : rootMotionModule.ProcessAirMovement(in ctx);
            velocity = stepResult.NewVelocity;
            if (controller.IsGrounded)
                velocity.Y = -0.5f;

            controller.SubmitMotion(new CharacterMotionCommand(
                velocity,
                CharacterMotionInputModel.Velocity,
                0.0f,
                FixedDelta));
            controller.ConsumeInputBuffer(FixedDelta);
            movement.PublishPhysicsStepFeedback(controller, FixedDelta);
        }

        controller.IsGrounded.ShouldBeFalse();
        controller.FootPosition.Y.ShouldBeLessThan(-0.2f);
        animator.StateMachine.Variables["IsGrounded"].BoolValue.ShouldBeFalse();
        layer.CurrentState.ShouldBe(fallState);
    }

    private void CreateFloor(Vector3 halfExtents, Vector3 center)
    {
        _joltScene!.CreateStaticRigidBody(
            new IPhysicsGeometry.Box(halfExtents),
            (center, Quaternion.Identity),
            Vector3.Zero,
            Quaternion.Identity,
            LayerMask.Everything).ShouldNotBeNull();
    }

    private JoltCharacterVirtualController CreateJoltController(Vector3 position)
    {
        var controller = new JoltCharacterVirtualController(_joltScene!, position)
        {
            Radius = 0.25f,
            TotalHeight = 1.6f,
            ContactOffset = 0.02f,
            StepOffset = 0.3f,
            SlopeLimit = MathF.Cos(MathF.PI / 4.0f),
        };
        return controller;
    }

    private static void SettleControllerOnFloor(JoltCharacterVirtualController controller)
    {
        for (int i = 0; i < 15; i++)
        {
            controller.SubmitMotion(new CharacterMotionCommand(
                -Vector3.UnitY * 2.0f,
                CharacterMotionInputModel.Velocity,
                0.0f,
                FixedDelta));
            controller.ConsumeInputBuffer(FixedDelta);
        }
    }

    private static SceneNode CreateTestHumanoidRig(out HumanoidComponent humanoid)
    {
        var root = new SceneNode("Root", new RigidBodyTransform());
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

    private sealed class GuidedSyntheticSomaSession : IMotionBricksInferenceSession
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
            Array.Clear(output.PredGlobalMotions);
            int chosenTokens = 6;
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

        private static Quaternion Read6D(float[] buffer, int offset)
            => SomaTensorCodec.RotationFrom6D(
                new Vector3(buffer[offset + 0], buffer[offset + 1], buffer[offset + 2]),
                new Vector3(buffer[offset + 3], buffer[offset + 4], buffer[offset + 5]));

        public void Dispose() { }
    }
}
