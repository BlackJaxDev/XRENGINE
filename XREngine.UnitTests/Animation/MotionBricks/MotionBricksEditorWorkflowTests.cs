using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Movement.Modules;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Editor.Tools;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.MotionBricks;

[TestFixture]
public sealed class MotionBricksEditorWorkflowTests
{
    [Test]
    public void MotionFactoryRegistry_RegistersAllBuiltInMotionTypesViaSourceGeneratorAndCreatesInstances()
    {
        Type[] registered = MotionFactoryRegistry.GetRegisteredTypes();
        registered.ShouldContain(typeof(AnimationClip));
        registered.ShouldContain(typeof(BlendTree1D));
        registered.ShouldContain(typeof(BlendTree2D));
        registered.ShouldContain(typeof(BlendTreeDirect));
        registered.ShouldContain(typeof(MotionBricksLocomotionMotion));

        Type[] expectedTypes =
        [
            typeof(AnimationClip),
            typeof(BlendTree1D),
            typeof(BlendTree2D),
            typeof(BlendTreeDirect),
            typeof(MotionBricksLocomotionMotion),
        ];

        for (int i = 0; i < expectedTypes.Length; i++)
        {
            Type expected = expectedTypes[i];
            MotionFactoryRegistry.TryCreate(expected, out MotionBase? byType).ShouldBeTrue();
            byType.ShouldNotBeNull();
            byType!.GetType().ShouldBe(expected);

            MotionFactoryRegistry.TryCreate(expected.Name, out MotionBase? byName).ShouldBeTrue();
            byName.ShouldNotBeNull();
            byName!.GetType().ShouldBe(expected);
        }
    }

    [Test]
    public void XREventPersistentCalls_InvokeThirdPersonBridgeAndDebugToggleWithZeroSteadyStateAllocations()
    {
        var root = new SceneNode("Player", new RigidBodyTransform());
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["StrafeMode"] = new AnimBool("StrafeMode", false);
        animator.StateMachine.Variables["LocomotionStyle"] = new AnimInt("LocomotionStyle", 0);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", true);
        animator.StateMachine.Variables["VerticalVelocity"] = new AnimFloat("VerticalVelocity", 0.0f);

        var bridge = root.AddComponent<CharacterRootMotionBridgeComponent>()!;
        bridge.Animator = animator;

        var debugViz = root.AddComponent<MotionBricksDebugDrawComponent>()!;
        debugViz.RenderEnabled = false;

        var pawn = root.AddComponent<CharacterPawnComponent>()!;
        pawn.RootMotionSteeringUpdated = new XREvent<SteeringSnapshot>
        {
            PersistentCalls =
            [
                new XRPersistentCall
                {
                    NodeId = root.ID,
                    TargetObjectId = bridge.ID,
                    MethodName = nameof(CharacterRootMotionBridgeComponent.ApplySteeringSnapshot),
                    ParameterTypeNames = [typeof(SteeringSnapshot).AssemblyQualifiedName!],
                }
            ],
        };
        pawn.LocomotionDebugToggled = new XREvent
        {
            PersistentCalls =
            [
                new XRPersistentCall
                {
                    NodeId = root.ID,
                    TargetObjectId = debugViz.ID,
                    MethodName = nameof(MotionBricksDebugDrawComponent.ToggleRenderEnabled),
                    ParameterTypeNames = [],
                }
            ],
        };

        var steering = new SteeringSnapshot(
            DesiredWorldMoveXZ: new Vector2(0.45f, -0.75f),
            CameraYawRadians: 1.2f,
            StrafeMode: true,
            SprintHeld: false,
            IsGrounded: true,
            LocomotionStyle: ELocomotionStyle.Crouched,
            VerticalVelocity: -0.5f);

        pawn.RootMotionSteeringUpdated.Invoke(steering);
        animator.StateMachine.Variables["MoveX"].FloatValue.ShouldBe(0.45f, 1.0e-5f);
        animator.StateMachine.Variables["MoveY"].FloatValue.ShouldBe(-0.75f, 1.0e-5f);
        animator.StateMachine.Variables["DesiredFacingYaw"].FloatValue.ShouldBe(1.2f, 1.0e-5f);
        animator.StateMachine.Variables["StrafeMode"].BoolValue.ShouldBeTrue();
        animator.StateMachine.Variables["LocomotionStyle"].IntValue.ShouldBe(1);
        animator.StateMachine.Variables["VerticalVelocity"].FloatValue.ShouldBe(-0.5f, 1.0e-5f);

        pawn.ToggleLocomotionDebug();
        debugViz.RenderEnabled.ShouldBeTrue();
        pawn.ToggleLocomotionDebug();
        debugViz.RenderEnabled.ShouldBeFalse();

        // Warm up cached delegates before measuring steady-state hot-path allocations.
        for (int i = 0; i < 4; i++)
        {
            pawn.RootMotionSteeringUpdated.Invoke(steering);
            pawn.ToggleLocomotionDebug();
        }

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            pawn.RootMotionSteeringUpdated.Invoke(steering);
            pawn.ToggleLocomotionDebug();
        }
        long afterBytes = GC.GetAllocatedBytesForCurrentThread();

        (afterBytes - beforeBytes).ShouldBe(0L, "Steady-state XREvent persistent calls must allocate zero bytes.");
    }

    [Test]
    public void DependencyScriptCatalog_DiscoversMotionBricksScriptsAndNativeBridgesWithParsedParameters()
    {
        string? repoRoot = DependencyScriptCatalog.FindRepositoryRoot();
        repoRoot.ShouldNotBeNullOrWhiteSpace();

        List<DependencyTaskDescriptor> tasks = DependencyScriptCatalog.DiscoverTasks(repoRoot!);
        tasks.Count.ShouldBeGreaterThan(5);

        DependencyTaskDescriptor? motionBricksScript = tasks.Find(t =>
            t.Kind == DependencyTaskKind.DependencyScript
            && string.Equals(t.DisplayName, "Get-MotionBricks", StringComparison.OrdinalIgnoreCase));
        motionBricksScript.ShouldNotBeNull();
        motionBricksScript!.Synopsis.ShouldContain("MotionBricks");

        DependencyScriptParameterDescriptor? precisionParam = Array.Find(
            motionBricksScript.Parameters,
            p => string.Equals(p.Name, "Precision", StringComparison.OrdinalIgnoreCase));
        precisionParam.ShouldNotBeNull();
        precisionParam!.AllowedValues.ShouldContain("fp16");
        precisionParam.AllowedValues.ShouldContain("fp32");
        precisionParam.AllowedValues.ShouldContain("both");
        precisionParam.DefaultValue.ShouldBe("fp16");

        DependencyScriptParameterDescriptor? forceParam = Array.Find(
            motionBricksScript.Parameters,
            p => string.Equals(p.Name, "Force", StringComparison.OrdinalIgnoreCase));
        forceParam.ShouldNotBeNull();
        forceParam!.Kind.ShouldBe(DependencyScriptParameterKind.Switch);

        DependencyTaskDescriptor? motionBricksBridge = tasks.Find(t =>
            t.Kind == DependencyTaskKind.NativeBridgeBuild
            && t.RelativePath.Contains("MotionBricksBridge.vcxproj", StringComparison.OrdinalIgnoreCase));
        motionBricksBridge.ShouldNotBeNull();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Precision"] = "both",
            ["Force"] = "true",
        };
        DependencyScriptCatalog.TryBuildCommandLine(
            repoRoot!,
            motionBricksScript,
            values,
            configuration: "Release",
            extraArguments: string.Empty,
            out string fileName,
            out string arguments,
            out _,
            out string? error).ShouldBeTrue(error);
        fileName.ShouldNotBeNullOrWhiteSpace();
        arguments.ShouldContain("-Precision both");
        arguments.ShouldContain("-Force");
    }

    [Test]
    public void CharacterPawnComponent_GamepadStanceTapHoldRunToggleAndDebugBindCleanly()
    {
        var root = new SceneNode("GamepadPlayer", new RigidBodyTransform());
        var movement = root.AddComponent<CharacterMovement3DComponent>()!;
        var pawn = root.AddComponent<CharacterPawnComponent>()!;
        var pivotNode = root.NewChildWithTransform<Transform>(out Transform pivotTransform, "CameraPivot");
        pivotNode.NewChildWithTransform<BoomTransform>(out BoomTransform boomTransform, "ThirdPersonBoom");
        boomTransform.MaxLength = 2.15f;

        var cameraRig = root.AddComponent<ThirdPersonCameraRigComponent>()!;
        cameraRig.CameraBoom = boomTransform;
        cameraRig.CameraPivotTransform = pivotTransform;
        cameraRig.CharacterMovement = movement;
        pawn.ThirdPersonCameraRig = cameraRig;

        var debugViz = root.AddComponent<MotionBricksDebugDrawComponent>()!;
        debugViz.RenderEnabled = false;
        pawn.LocomotionDebugToggled = new XREvent
        {
            PersistentCalls =
            [
                new XRPersistentCall
                {
                    NodeId = root.ID,
                    TargetObjectId = debugViz.ID,
                    MethodName = nameof(MotionBricksDebugDrawComponent.ToggleRenderEnabled),
                    ParameterTypeNames = [],
                }
            ],
        };

        // Tap right face button -> toggle crouch on release.
        pawn.OnGamepadStancePressed();
        movement.CrouchState.ShouldBe(CharacterMovement3DComponent.ECrouchState.Standing);
        pawn.OnGamepadStanceReleased();
        movement.CrouchState.ShouldBe(CharacterMovement3DComponent.ECrouchState.Crouched);

        // Hold right face button -> toggle prone on hold; release does not toggle crouch.
        pawn.OnGamepadStancePressed();
        pawn.OnGamepadStanceHeld();
        movement.CrouchState.ShouldBe(CharacterMovement3DComponent.ECrouchState.Prone);
        pawn.OnGamepadStanceReleased();
        movement.CrouchState.ShouldBe(CharacterMovement3DComponent.ECrouchState.Prone);

        // Toggle run with left face button.
        movement.IsRunning.ShouldBeFalse();
        pawn.ToggleGamepadRun();
        movement.IsRunning.ShouldBeTrue();
        pawn.ToggleGamepadRun();
        movement.IsRunning.ShouldBeFalse();

        // Shoulder views (LB / RB), centered zoom (D-Pad Up), and top face button debug toggle.
        pawn.ToggleLeftShoulderView();
        cameraRig.ViewMode.ShouldBe(EThirdPersonCameraViewMode.LeftShoulder);
        pawn.ToggleRightShoulderView();
        cameraRig.ViewMode.ShouldBe(EThirdPersonCameraViewMode.RightShoulder);
        cameraRig.StepFraming(0.5f);
        boomTransform.ShoulderOffset.X.ShouldBeGreaterThan(0.1f);

        pawn.OnGamepadCameraZoomIn(true);
        cameraRig.StepFraming(0.25f);
        cameraRig.ViewMode.ShouldBe(EThirdPersonCameraViewMode.Centered);
        pawn.OnGamepadCameraZoomIn(false);

        pawn.ToggleLocomotionDebug();
        debugViz.RenderEnabled.ShouldBeTrue();
    }

    [Test]
    public void CharacterRootMotionBridge_ConfiguresLateBoundProceduralMotionAndPreservesModuleTuning()
    {
        var root = new SceneNode("LateBindCharacter", new RigidBodyTransform());
        var movement = root.AddComponent<CharacterMovement3DComponent>()!;
        movement.MovementModule = new ModernMovementModule("CustomTuned")
        {
            JumpForce = 6.5f,
            GravityScale = 3.2f,
        };

        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        var idleClip = new AnimationClip { Name = "Idle", LengthInSeconds = 1.0f, Looped = true };
        var locomotionMotion = new MotionBricksLocomotionMotion { Name = "Locomotion" };
        var idleState = new AnimState(idleClip, "Idle");
        var locomotionState = new AnimState(locomotionMotion, "Locomotion");
        var layer = new AnimLayer(idleState, locomotionState) { InitialState = idleState };
        animator.StateMachine.Layers.Add(layer);

        var avatarNode = root.NewChildWithTransform<Transform>(out Transform avatarTransform, "Avatar");
        avatarTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.75f);

        var bridge = root.AddComponent<CharacterRootMotionBridgeComponent>()!;
        bridge.Animator = animator;
        bridge.CharacterMovement = movement;
        bridge.AvatarFacingTransform = avatarTransform;
        bridge.BindEndpoints();

        // Auto-configured RootMotionMovementModule keeps user-customized jump and gravity settings.
        var rootMotionModule = movement.MovementModule.ShouldBeOfType<RootMotionMovementModule>();
        rootMotionModule.JumpForce.ShouldBe(6.5f, 1.0e-4f);
        rootMotionModule.GravityScale.ShouldBe(3.2f, 1.0e-4f);

        // Bind a driver AFTER BindEndpoints() already ran while Idle was the initial state.
        var lateDriver = new RecordingLocomotionDriver();
        locomotionMotion.RuntimeDriverOverride = lateDriver;
        locomotionMotion.GetAnimationValues(null, animator.StateMachine.Variables, 1.0f);

        lateDriver.InitialFacingYawSet.ShouldBeTrue();
        lateDriver.InitialFacingYawRadians.ShouldBe(0.75f, 1.0e-4f);
        lateDriver.WorldRootMotionScaleSet.ShouldBeTrue();
        lateDriver.TrajectoryProber.ShouldNotBeNull();

        bridge.UnbindEndpoints();
        lateDriver.TrajectoryProber.ShouldBeNull();
    }

    private sealed class RecordingLocomotionDriver : IMotionBricksLocomotionDriver
    {
        public bool IsReady => true;
        public string DiagnosticStatus => "Ready";
        public bool InitialFacingYawSet { get; private set; }
        public float InitialFacingYawRadians { get; private set; }
        public bool WorldRootMotionScaleSet { get; private set; }
        public IMotionBricksTrajectoryProber? TrajectoryProber { get; private set; }

        public void OnStateEntered() { }
        public void OnStateExited() { }
        public void ResetPlayback() { }
        public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination) { }
        public void Dispose() { }

        public void SetInitialFacingYaw(float facingYawRadians)
        {
            InitialFacingYawSet = true;
            InitialFacingYawRadians = facingYawRadians;
        }

        public void SetWorldRootMotionScale(float worldRootMotionScale)
            => WorldRootMotionScaleSet = true;

        public void SetTrajectoryProber(IMotionBricksTrajectoryProber? prober)
            => TrajectoryProber = prober;
    }
}
