using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Physics;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Physx;
using XREngine.Scene.Transforms;

namespace MonkeyBallVR;

/// <summary>
/// Coordinates the authored MonkeyBall rigid bodies, stage controls, camera,
/// round state, and HUD.
/// </summary>
public sealed class MonkeyBallGameComponent : XRComponent, IMonkeyBallGameInputTarget
{
    private const float DegreesToRadians = MathF.PI / 180.0f;
    private const float RequiredPhysicsFixedRateHz = 120.0f;
    private const float RequiredPhysicsFixedDelta = 1.0f / RequiredPhysicsFixedRateHz;
    private const float PhysicsFixedDeltaTolerance = 1.0e-6f;

    private string _courseNodeName = "Tilting Course";
    private string _ballNodeName = "Player Ball";
    private string _desktopCameraNodeName = "Desktop Camera";
    private string _hudNodeName = "Procedural Scoreboard";
    private float _ballRadius = 0.5f;
    private Vector2 _startPosition = new(0.0f, 5.0f);
    private Vector2 _goalPosition = new(0.0f, -14.0f);
    private float _goalRadius = 1.15f;
    private float _roundDurationSeconds = 60.0f;
    private float _maxTiltDegrees = 12.0f;
    private float _stageTiltInterpolationSpeed = 2.0f;
    private int _initialLives = 3;
    private float _maxBallSpeed = 12.0f;
    private float _fallThresholdY = -3.5f;
    private float _fallResetDelaySeconds = 0.75f;
    private Vector3 _desktopCameraOffset = new(0.0f, 2.5f, 5.5f);
    private float _desktopCameraPitchDegrees = -14.0f;
    private float _desktopCameraYawResponse = 0.67f;
    private float _cameraHeadingVelocityThreshold = 0.2f;

    private DynamicRigidBodyComponent? _courseBody;
    private DynamicRigidBodyComponent? _ballBody;
    private RigidBodyTransform? _courseTransform;
    private RigidBodyTransform? _ballTransform;
    private Transform? _desktopCameraTransform;
    private CameraComponent? _desktopCamera;
    private DebugDrawComponent? _hud;
    private MonkeyBallPawnComponent? _pawn;
    private DirectionalLightComponent? _directionalLight;
    private Vector2 _tilt;
    private float _cameraYaw;
    private Quaternion _cameraHeadingRotation = Quaternion.Identity;
    private Quaternion _stageRotation = Quaternion.Identity;
    private float _stateTimer;
    private float _timeRemaining;
    private AbstractPhysicsScene? _diagnosticPhysicsScene;
    private float _hudRefreshTimer;
    private int _lives;
    private int _score;
    private bool _pendingBallReset;
    private bool? _pendingBallSimulationEnabled;
    private bool _possessionReadyRecorded;
    private bool _physicsRuntimeReadyRecorded;
    private MonkeyBallRoundState _state = MonkeyBallRoundState.Playing;
    private MonkeyBallRoundState _stateBeforePause = MonkeyBallRoundState.Playing;

    public string CourseNodeName
    {
        get => _courseNodeName;
        set => SetField(ref _courseNodeName, value);
    }

    public string BallNodeName
    {
        get => _ballNodeName;
        set => SetField(ref _ballNodeName, value);
    }

    public string DesktopCameraNodeName
    {
        get => _desktopCameraNodeName;
        set => SetField(ref _desktopCameraNodeName, value);
    }

    public string HudNodeName
    {
        get => _hudNodeName;
        set => SetField(ref _hudNodeName, value);
    }

    public float BallRadius
    {
        get => _ballRadius;
        set => SetField(ref _ballRadius, MathF.Max(0.01f, value));
    }

    public Vector2 StartPosition
    {
        get => _startPosition;
        set => SetField(ref _startPosition, value);
    }

    public Vector2 GoalPosition
    {
        get => _goalPosition;
        set => SetField(ref _goalPosition, value);
    }

    public float GoalRadius
    {
        get => _goalRadius;
        set => SetField(ref _goalRadius, MathF.Max(0.01f, value));
    }

    public float RoundDurationSeconds
    {
        get => _roundDurationSeconds;
        set => SetField(ref _roundDurationSeconds, MathF.Max(1.0f, value));
    }

    public float MaxTiltDegrees
    {
        get => _maxTiltDegrees;
        set => SetField(ref _maxTiltDegrees, Math.Clamp(value, 0.0f, 45.0f));
    }

    public float StageTiltInterpolationSpeed
    {
        get => _stageTiltInterpolationSpeed;
        set => SetField(ref _stageTiltInterpolationSpeed, MathF.Max(0.01f, value));
    }

    public int InitialLives
    {
        get => _initialLives;
        set => SetField(ref _initialLives, Math.Max(1, value));
    }

    public float MaxBallSpeed
    {
        get => _maxBallSpeed;
        set => SetField(ref _maxBallSpeed, MathF.Max(0.01f, value));
    }

    public float FallThresholdY
    {
        get => _fallThresholdY;
        set => SetField(ref _fallThresholdY, value);
    }

    public float FallResetDelaySeconds
    {
        get => _fallResetDelaySeconds;
        set => SetField(ref _fallResetDelaySeconds, MathF.Max(0.0f, value));
    }

    public Vector3 DesktopCameraOffset
    {
        get => _desktopCameraOffset;
        set => SetField(ref _desktopCameraOffset, value);
    }

    public float DesktopCameraPitchDegrees
    {
        get => _desktopCameraPitchDegrees;
        set => SetField(ref _desktopCameraPitchDegrees, Math.Clamp(value, -89.0f, 89.0f));
    }

    public float DesktopCameraYawResponse
    {
        get => _desktopCameraYawResponse;
        set => SetField(ref _desktopCameraYawResponse, MathF.Max(0.0f, value));
    }

    public float CameraHeadingVelocityThreshold
    {
        get => _cameraHeadingVelocityThreshold;
        set => SetField(ref _cameraHeadingVelocityThreshold, MathF.Max(0.0f, value));
    }

    public void SetTilt(Vector2 tilt)
    {
        _tilt = Vector2.Clamp(tilt, new Vector2(-1.0f), new Vector2(1.0f));
        if (MonkeyBallRuntimeDiagnostics.Enabled)
            MonkeyBallRuntimeDiagnostics.RecordTilt(_tilt);
    }

    public void ResetRound()
    {
        if (_state is MonkeyBallRoundState.Won or MonkeyBallRoundState.Lost)
        {
            _lives = InitialLives;
            _score = 0;
        }

        ResetBall(resetTimer: true);
    }

    public void TogglePause()
    {
        if (_state == MonkeyBallRoundState.Paused)
        {
            _state = _stateBeforePause;
            SetBallSimulationEnabled(true);
            return;
        }

        _stateBeforePause = _state;
        _state = MonkeyBallRoundState.Paused;
        SetBallSimulationEnabled(false);
    }

    protected override void OnBeginPlay()
    {
        MonkeyBallRuntimeValidation.RecordBeginPlay();
        MonkeyBallRuntimeDiagnostics.RecordBeginPlay();
        MonkeyBallRuntimeDiagnostics.RecordEvent("game-begin-play-enter");
        base.OnBeginPlay();
        ResolveSceneReferences();
        SubscribeToDiagnosticPhysicsSteps();
        _physicsRuntimeReadyRecorded = RecordPhysicsRuntimeState();
        RecordDirectionalShadowRuntimeState("resolved");
        MonkeyBallRuntimeDiagnostics.RecordEvent(
            "game-scene-references-resolved",
            $"courseActor={_courseBody!.RigidBody is not null} ballActor={_ballBody!.RigidBody is not null}");
        _pawn!.PossessByLocalPlayer(ELocalPlayerIndex.One);
        MonkeyBallRuntimeDiagnostics.RecordEvent("game-pawn-possession-requested");
        RecordPossessionState();
        _lives = InitialLives;
        _score = 0;
        ResetBall(resetTimer: true);
        UpdateDesktopCamera(GetBallRenderPosition(), Vector3.Zero, 0.0f);
        UpdateHud(force: true);
        MonkeyBallRuntimeDiagnostics.RecordEvent("game-begin-play-complete");
    }

    protected override void OnComponentActivated()
    {
        MonkeyBallRuntimeValidation.RecordComponentActivated();
        MonkeyBallRuntimeDiagnostics.RecordComponentActivated();
        MonkeyBallRuntimeDiagnostics.RecordEvent("game-component-activated-enter");
        base.OnComponentActivated();
        RegisterTick(ETickGroup.PrePhysics, ETickOrder.Logic, PrePhysicsTick);
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, Tick);
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, CameraTick);
        MonkeyBallRuntimeDiagnostics.RecordEvent("game-component-activated-complete");
    }

    protected override void OnComponentDeactivated()
    {
        UnsubscribeFromDiagnosticPhysicsSteps();
        UnregisterTick(ETickGroup.PrePhysics, ETickOrder.Logic, PrePhysicsTick);
        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, Tick);
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CameraTick);
        _tilt = Vector2.Zero;
        _pendingBallSimulationEnabled = null;
        _physicsRuntimeReadyRecorded = false;
        base.OnComponentDeactivated();
    }

    private void ResolveSceneReferences()
    {
        SceneNode root = SceneNode;
        SceneNode courseNode = RequireNode(root, CourseNodeName);
        SceneNode ballNode = RequireNode(root, BallNodeName);
        SceneNode cameraNode = RequireNode(root, DesktopCameraNodeName);

        _courseTransform = courseNode.GetTransformAs<RigidBodyTransform>(false)
            ?? throw new InvalidOperationException(
                $"MonkeyBall course node '{CourseNodeName}' requires a {nameof(RigidBodyTransform)}.");
        _ballTransform = ballNode.GetTransformAs<RigidBodyTransform>(false)
            ?? throw new InvalidOperationException(
                $"MonkeyBall ball node '{BallNodeName}' requires a {nameof(RigidBodyTransform)}.");
        if (_ballTransform.InterpolationMode != RigidBodyTransform.EInterpolationMode.Interpolate)
            throw new InvalidOperationException(
                $"MonkeyBall ball node '{BallNodeName}' must author " +
                $"{nameof(RigidBodyTransform.InterpolationMode)} as Interpolate.");
        _courseBody = courseNode.GetComponent<DynamicRigidBodyComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall course node '{CourseNodeName}' has no kinematic {nameof(DynamicRigidBodyComponent)}.");
        _ballBody = ballNode.GetComponent<DynamicRigidBodyComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall ball node '{BallNodeName}' has no {nameof(DynamicRigidBodyComponent)}.");
        _desktopCameraTransform = cameraNode.GetTransformAs<Transform>(true)
            ?? throw new InvalidOperationException(
                $"MonkeyBall camera node '{DesktopCameraNodeName}' requires a {nameof(Transform)}.");
        _hud = RequireNode(root, HudNodeName).GetComponent<DebugDrawComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall HUD node '{HudNodeName}' has no {nameof(DebugDrawComponent)}.");
        _pawn = root.FindFirstDescendantComponent<MonkeyBallPawnComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall world has no {nameof(MonkeyBallPawnComponent)}.");
        _directionalLight = root.FindFirstDescendantComponent<DirectionalLightComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall world has no authored {nameof(DirectionalLightComponent)}.");
        if (_courseBody.RigidBody is null)
            throw new InvalidOperationException(
                "MonkeyBall's cooked course body did not create a native rigid body. " +
                "Verify cooked component activation and the active physics backend.");
        if (_ballBody.RigidBody is null)
            throw new InvalidOperationException(
                "MonkeyBall's cooked ball body did not create a native rigid body. " +
                "Verify cooked component activation and the active physics backend.");
        _stageRotation = Quaternion.Normalize(_courseBody.RigidBody.Transform.rotation);

        _desktopCamera = cameraNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall camera node '{DesktopCameraNodeName}' has no {nameof(CameraComponent)}.");
        ConfigureDesktopCamera(_desktopCamera);
        _pawn.CameraComponent = _desktopCamera;
        _pawn.Bind(this);
    }

    private static void ConfigureDesktopCamera(CameraComponent camera)
    {
        XRCamera renderCamera = camera.Camera;
        renderCamera.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
        renderCamera.RenderPipeline.OverrideProtected = true;
        camera.DirectionalShadowRenderingMode = EDirectionalShadowRenderingMode.NonCascaded;

        var colorStage = renderCamera.GetPostProcessStageState<ColorGradingSettings>();
        if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
        {
            grading.AutoExposure = false;
            grading.Exposure = 1.0f;
        }
        else
        {
            colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
            colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
        }
    }

    private static SceneNode RequireNode(SceneNode root, string name)
        => root.FindDescendantByName(name, StringComparison.Ordinal)
            ?? throw new InvalidOperationException(
                $"MonkeyBall world is missing scene node '{name}'.");

    private void SubscribeToDiagnosticPhysicsSteps()
    {
        if (!MonkeyBallRuntimeDiagnostics.Enabled &&
            !MonkeyBallRuntimeValidation.Enabled)
            return;

        AbstractPhysicsScene? scene = WorldAs<XRWorldInstance>()?.PhysicsScene;
        if (ReferenceEquals(_diagnosticPhysicsScene, scene))
            return;

        UnsubscribeFromDiagnosticPhysicsSteps();
        _diagnosticPhysicsScene = scene;
        if (_diagnosticPhysicsScene is not null)
            _diagnosticPhysicsScene.OnSimulationStep += OnDiagnosticPhysicsStep;
    }

    private void UnsubscribeFromDiagnosticPhysicsSteps()
    {
        if (_diagnosticPhysicsScene is not null)
            _diagnosticPhysicsScene.OnSimulationStep -= OnDiagnosticPhysicsStep;
        _diagnosticPhysicsScene = null;
    }

    private static void OnDiagnosticPhysicsStep()
    {
        MonkeyBallRuntimeValidation.RecordPhysicsStep();
        MonkeyBallRuntimeDiagnostics.RecordPhysicsStep();
    }

    private bool RecordPhysicsRuntimeState()
    {
        if (WorldAs<XRWorldInstance>() is not XRWorldInstance world ||
            _courseBody is null ||
            _ballBody is null ||
            _ballTransform is null)
        {
            return false;
        }

        string sceneType = world.PhysicsScene.GetType().Name;
        string courseActorType = _courseBody.RigidBody?.GetType().Name ?? "null";
        string ballActorType = _ballBody.RigidBody?.GetType().Name ?? "null";
        bool courseInScene = IsActorInScene(_courseBody.RigidBody, world.PhysicsScene);
        bool ballInScene = IsActorInScene(_ballBody.RigidBody, world.PhysicsScene);
        int courseColliderCount = CountEffectiveColliderShapes(_courseBody);
        int ballColliderCount = CountEffectiveColliderShapes(_ballBody);
        float authoredTimestep =
            world.TargetWorld?.Settings.PhysicsTimestep ?? 0.0f;
        float engineFixedDelta = Engine.FixedDelta;
        bool authoredTimestepReady =
            MathF.Abs(authoredTimestep - RequiredPhysicsFixedDelta) <= PhysicsFixedDeltaTolerance;
        bool engineFixedDeltaReady =
            MathF.Abs(engineFixedDelta - RequiredPhysicsFixedDelta) <= PhysicsFixedDeltaTolerance;
        bool runtimeReady =
            _courseBody.IsActiveInHierarchy &&
            _ballBody.IsActiveInHierarchy &&
            !string.Equals(courseActorType, "null", StringComparison.Ordinal) &&
            !string.Equals(ballActorType, "null", StringComparison.Ordinal) &&
            courseInScene &&
            ballInScene &&
            _courseBody.BodyFlags.HasFlag(PhysicsRigidBodyFlags.Kinematic) &&
            courseColliderCount > 0 &&
            ballColliderCount > 0 &&
            _ballBody.GravityEnabled &&
            _ballBody.SimulationEnabled &&
            _ballTransform.InterpolationMode == RigidBodyTransform.EInterpolationMode.Interpolate &&
            authoredTimestepReady &&
            engineFixedDeltaReady;
        MonkeyBallRuntimeValidation.RecordPhysicsRuntime(
            sceneType,
            authoredTimestep,
            engineFixedDelta,
            _ballTransform.InterpolationMode,
            _courseBody.IsActiveInHierarchy,
            _ballBody.IsActiveInHierarchy,
            courseActorType,
            ballActorType,
            courseInScene,
            ballInScene,
            _courseBody.BodyFlags,
            courseColliderCount,
            ballColliderCount,
            _ballBody.GravityEnabled,
            _ballBody.SimulationEnabled);
        if (!MonkeyBallRuntimeDiagnostics.Enabled)
            return runtimeReady;

        MonkeyBallRuntimeDiagnostics.RecordPhysicsRuntime(
            sceneType,
            world.TargetWorld?.Settings.Gravity ?? Vector3.Zero,
            authoredTimestep,
            world.TargetWorld?.Settings.PhysicsSubsteps ?? 0,
            engineFixedDelta,
            _ballTransform.InterpolationMode.ToString(),
            _courseBody.IsActiveInHierarchy,
            _ballBody.IsActiveInHierarchy,
            courseActorType,
            ballActorType,
            courseInScene,
            ballInScene,
            _courseBody.BodyFlags,
            courseColliderCount,
            ballColliderCount,
            _ballBody.GravityEnabled,
            _ballBody.SimulationEnabled);
        return runtimeReady;
    }

    /// <summary>
    /// Counts enabled compound shapes, or the legacy single geometry when no
    /// compound shape list is authored.
    /// </summary>
    internal static int CountEffectiveColliderShapes(DynamicRigidBodyComponent body)
    {
        ArgumentNullException.ThrowIfNull(body);

        List<PhysicsColliderShape> shapes = body.ColliderShapes;
        if (shapes.Count == 0)
            return body.Geometry is null ? 0 : 1;

        int count = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            PhysicsColliderShape shape = shapes[i];
            if (shape.Enabled && shape.Geometry is not null)
                count++;
        }

        return count;
    }

    private void RecordDirectionalShadowRuntimeState(string phase)
    {
        if (_directionalLight is not DirectionalLightComponent light)
            return;

        XRCamera? shadowCamera = light.ShadowCamera;
        string desktopCameraMode =
            _desktopCamera?.DirectionalShadowRenderingMode.ToString() ?? "null";
        MonkeyBallRuntimeValidation.RecordDirectionalShadow(
            light.IsActiveInHierarchy,
            light.Type.ToString(),
            light.CastsShadows,
            light.UseShadowAtlas,
            light.EnableCascadedShadows,
            light.ShadowMapResolutionWidth,
            light.ShadowMapResolutionHeight,
            light.ShadowMap is not null,
            light.HasPrimaryShadowReceiverTexture,
            shadowCamera is not null,
            desktopCameraMode,
            light.PrimaryShadowCasterCount,
            light.StandaloneShadowRenderRequestCount,
            light.StandaloneShadowRenderPassCount);
        if (!MonkeyBallRuntimeDiagnostics.Enabled)
            return;

        MonkeyBallRuntimeDiagnostics.RecordDirectionalShadow(
            phase,
            light.ActivationCount,
            light.IsActiveInHierarchy,
            light.Type.ToString(),
            light.CastsShadows,
            light.UseShadowAtlas,
            light.EnableCascadedShadows,
            light.ShadowMapResolutionWidth,
            light.ShadowMapResolutionHeight,
            light.Scale,
            light.ShadowMapStorageFormat.ToString(),
            light.ShadowMapEncoding.ToString(),
            light.ShadowMap is not null,
            light.HasPrimaryShadowReceiverTexture,
            shadowCamera is not null,
            shadowCamera?.NearZ ?? 0.0f,
            shadowCamera?.FarZ ?? 0.0f,
            desktopCameraMode,
            light.PrimaryShadowCasterCount,
            light.StandaloneShadowRenderRequestCount,
            light.StandaloneShadowRenderPassCount);
    }

    private void RecordPossessionState()
    {
        if (_pawn is null)
            return;

        var controller = _pawn.Controller;
        bool isLocal = controller?.IsLocal ?? false;
        bool authoredPawn = ReferenceEquals(controller?.ControlledPawnComponent, _pawn);
        string inputType = controller?.InputDevice?.GetType().Name ?? "null";
        string viewportType = controller?.Viewport?.GetType().Name ?? "null";
        string cameraType = _pawn.CameraComponent?.GetType().Name ?? "null";
        MonkeyBallRuntimeValidation.RecordPossession(
            isLocal,
            authoredPawn,
            inputType,
            viewportType,
            cameraType);
        if (!MonkeyBallRuntimeDiagnostics.Enabled)
            return;

        MonkeyBallRuntimeDiagnostics.RecordPossession(
            controller?.GetType().Name ?? "null",
            isLocal,
            controller?.LocalPlayerIndex?.ToString() ?? "null",
            authoredPawn,
            inputType,
            viewportType,
            cameraType);
    }

    private void TryRecordPossessionReady()
    {
        if (_possessionReadyRecorded ||
            _pawn?.Controller?.Viewport is null ||
            _pawn.Controller.InputDevice is null)
        {
            return;
        }

        _possessionReadyRecorded = true;
        MonkeyBallRuntimeDiagnostics.RecordEvent("possession-ready");
        RecordPossessionState();
    }

    private void PrePhysicsTick()
    {
        TryApplyPendingBallSimulationState();
        TryApplyPendingBallReset();

        if (_courseBody is null || _courseTransform is null)
            return;

        if (!_physicsRuntimeReadyRecorded)
            _physicsRuntimeReadyRecorded = RecordPhysicsRuntimeState();

        if (_state == MonkeyBallRoundState.Playing)
            ClampBallSpeedOnPhysicsThread();

        Vector2 input = _state == MonkeyBallRoundState.Playing ? _tilt : Vector2.Zero;
        if (MonkeyBallRuntimeValidation.TryGetScriptedTilt(out Vector2 validationTilt))
            input = validationTilt;

        Vector2 worldTilt = CameraRelativeInputToWorld(input, _cameraYaw);
        Quaternion targetRotation = CalculateStageTargetRotation(
            input,
            _cameraHeadingRotation,
            MaxTiltDegrees);
        _stageRotation = InterpolateRotation(
            _stageRotation,
            targetRotation,
            StageTiltInterpolationSpeed,
            Engine.FixedDelta);
        Vector3 ballPosition = GetBallPhysicsPosition();
        Vector3 translation = ResolveStagePivotTranslation(ballPosition, _stageRotation);
        var target = (translation, _stageRotation);
        IAbstractDynamicRigidBody courseActor = _courseBody.RigidBody
            ?? throw new InvalidOperationException(
                "MonkeyBall cannot tilt the course because its native kinematic actor is missing.");
        bool ballActorExists = _ballBody?.RigidBody is not null;

        _courseBody.KinematicTarget = target;
        courseActor.KinematicTarget = target;

        MonkeyBallRuntimeValidation.RecordPrePhysics(
            input,
            worldTilt,
            _cameraYaw,
            ballPosition,
            translation,
            _stageRotation,
            true,
            ballActorExists);
        if (MonkeyBallRuntimeDiagnostics.Enabled)
            MonkeyBallRuntimeDiagnostics.RecordPrePhysics(
                input,
                worldTilt,
                _cameraYaw,
                ballPosition,
                translation,
                targetRotation,
                _stageRotation,
                true,
                ballActorExists);
    }

    private void Tick()
    {
        TryRecordPossessionReady();
        float delta = Math.Clamp(Engine.Delta, 0.0f, 0.1f);
        Vector3 position = GetBallRenderPosition();
        Vector3 velocity = GetBallCachedVelocity();
        IAbstractDynamicRigidBody? ballActor = _ballBody?.RigidBody;
        IAbstractDynamicRigidBody? courseActor = _courseBody?.RigidBody;
        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        bool courseInScene =
            world is not null && IsActorInScene(courseActor, world.PhysicsScene);
        bool ballInScene =
            world is not null && IsActorInScene(ballActor, world.PhysicsScene);

        MonkeyBallRuntimeValidation.RecordNormalTick(
            position,
            velocity,
            world?.PhysicsEnabled ?? false,
            world?.PhysicsScene.GetType().Name ?? "null",
            courseInScene,
            ballInScene);
        if (MonkeyBallRuntimeDiagnostics.Enabled)
        {
            MonkeyBallRuntimeDiagnostics.RecordNormalTick(
                position,
                velocity,
                GetBallCachedAngularVelocity(),
                ballActor?.IsSleeping ?? true,
                world?.PhysicsEnabled ?? false,
                world?.PhysicsScene.GetType().Name ?? "null",
                courseInScene,
                ballInScene);
        }
        RecordDirectionalShadowRuntimeState("tick");

        if (_state != MonkeyBallRoundState.Paused)
            UpdateRoundState(position, delta);

        UpdateHud(force: false);
    }

    /// <summary>
    /// Updates the desktop camera after the rigid-body presentation pose and
    /// normal gameplay logic have completed for this frame.
    /// </summary>
    private void CameraTick()
    {
        RecordDesktopCameraPresentation();
        UpdateDesktopCamera(
            GetBallRenderPosition(),
            GetBallCachedVelocity(),
            Math.Clamp(Engine.Delta, 0.0f, 0.1f));
    }

    private void RecordDesktopCameraPresentation()
    {
        if (_ballTransform is null || _desktopCameraTransform is null)
            return;

        // The render thread publishes matrices independently. Read the ball on
        // both sides of the camera sample and discard a torn cross-frame pair.
        Vector3 ballPositionBefore = _ballTransform.RenderTranslation;
        Vector3 cameraPosition = _desktopCameraTransform.RenderTranslation;
        Quaternion cameraRotation = _desktopCameraTransform.RenderRotation;
        Vector3 ballPositionAfter = _ballTransform.RenderTranslation;
        if (Vector3.DistanceSquared(ballPositionBefore, ballPositionAfter) > 1.0e-8f)
            return;

        float expectedOffsetLength = DesktopCameraOffset.Length();
        MonkeyBallRuntimeValidation.RecordDesktopCameraPresentation(
            ballPositionAfter,
            cameraPosition,
            cameraRotation,
            expectedOffsetLength);
        if (MonkeyBallRuntimeDiagnostics.Enabled)
            MonkeyBallRuntimeDiagnostics.RecordDesktopCameraPresentation(
                ballPositionAfter,
                cameraPosition,
                cameraRotation,
                expectedOffsetLength);
    }

    private void UpdateRoundState(Vector3 position, float delta)
    {
        switch (_state)
        {
            case MonkeyBallRoundState.Playing:
                _timeRemaining = MathF.Max(0.0f, _timeRemaining - delta);
                if (_timeRemaining <= 0.0f)
                {
                    _state = MonkeyBallRoundState.Lost;
                    _stateTimer = 0.0f;
                    SetBallSimulationEnabled(false);
                    return;
                }

                if (position.Y < FallThresholdY)
                {
                    _state = MonkeyBallRoundState.Falling;
                    _stateTimer = 0.0f;
                    return;
                }

                Vector2 horizontalPosition = new(position.X, position.Z);
                if (Vector2.DistanceSquared(horizontalPosition, GoalPosition) <= GoalRadius * GoalRadius)
                {
                    _state = MonkeyBallRoundState.Won;
                    _stateTimer = 0.0f;
                    _score += Math.Max(100, (int)(_timeRemaining * 100.0f));
                    SetBallSimulationEnabled(false);
                }
                break;

            case MonkeyBallRoundState.Falling:
                _stateTimer += delta;
                if (_stateTimer < FallResetDelaySeconds)
                    return;

                _lives--;
                if (_lives <= 0)
                {
                    _state = MonkeyBallRoundState.Lost;
                    _stateTimer = 0.0f;
                    SetBallSimulationEnabled(false);
                    return;
                }

                ResetBall(resetTimer: false);
                break;

            case MonkeyBallRoundState.Won:
            case MonkeyBallRoundState.Lost:
                _stateTimer += delta;
                break;
        }
    }

    private void ClampBallSpeedOnPhysicsThread()
    {
        IAbstractDynamicRigidBody actor = RequireBallActor();
        Vector3 velocity = actor.LinearVelocity;

        float maxSpeedSquared = MaxBallSpeed * MaxBallSpeed;
        float speedSquared = velocity.LengthSquared();
        if (speedSquared <= maxSpeedSquared)
            return;

        actor.SetLinearVelocity(
            velocity * (MaxBallSpeed / MathF.Sqrt(speedSquared)));
    }

    private void ResetBall(bool resetTimer)
    {
        _state = MonkeyBallRoundState.Playing;
        _stateTimer = 0.0f;
        _tilt = Vector2.Zero;
        ResetCameraHeading();
        if (resetTimer)
            _timeRemaining = RoundDurationSeconds;

        _pendingBallReset = true;
    }

    private void TryApplyPendingBallReset()
    {
        if (!_pendingBallReset || _ballBody is null || _ballTransform is null)
            return;

        ResetStage();
        Vector3 position = new(StartPosition.X, BallRadius + 0.08f, StartPosition.Y);
        _ballTransform.SetPositionAndRotation(position, Quaternion.Identity);
        _ballBody.SimulationEnabled = true;
        _pendingBallSimulationEnabled = null;
        _ballBody.LinearVelocity = Vector3.Zero;
        _ballBody.AngularVelocity = Vector3.Zero;

        IAbstractDynamicRigidBody rigidBody = _ballBody.RigidBody
            ?? throw new InvalidOperationException(
                "MonkeyBall cannot reset the ball because its native dynamic actor is missing.");

        rigidBody.SetTransform(position, Quaternion.Identity);
        rigidBody.SetLinearVelocity(Vector3.Zero);
        rigidBody.SetAngularVelocity(Vector3.Zero);
        rigidBody.WakeUp();
        _ballTransform.OnPhysicsStepped();
        MonkeyBallRuntimeDiagnostics.RecordBallReset(
            position,
            rigidBody.Transform.position,
            rigidBody.Transform.rotation,
            rigidBody.LinearVelocity,
            rigidBody.AngularVelocity,
            rigidBody.IsSleeping,
            _ballTransform.LastPhysicsTransform.position,
            _ballTransform.LastPhysicsLinearVelocity,
            _ballTransform.LastPhysicsAngularVelocity);
        _pendingBallReset = false;
    }

    private void ResetStage()
    {
        _stageRotation = Quaternion.Identity;
        if (_courseBody?.RigidBody is not IAbstractDynamicRigidBody courseActor)
            return;

        var target = (Vector3.Zero, Quaternion.Identity);
        _courseBody.KinematicTarget = target;
        courseActor.KinematicTarget = target;
        _courseTransform?.SetPositionAndRotation(target.Item1, target.Item2);
        _courseTransform?.OnPhysicsStepped();
    }

    private void SetBallSimulationEnabled(bool enabled)
        => _pendingBallSimulationEnabled = enabled;

    private void TryApplyPendingBallSimulationState()
    {
        bool? enabled = _pendingBallSimulationEnabled;
        if (!enabled.HasValue || _ballBody is null)
            return;

        _ballBody.SimulationEnabled = enabled.Value;
        _pendingBallSimulationEnabled = null;
    }

    private static bool IsActorInScene(
        IAbstractDynamicRigidBody? actor,
        AbstractPhysicsScene scene)
        => actor is PhysxActor physxActor && ReferenceEquals(physxActor.Scene, scene);

    private Vector3 GetBallPhysicsPosition()
        => RequireBallActor().Transform.position;

    private Vector3 GetBallRenderPosition()
        => RequireBallTransform().WorldTranslation;

    private Vector3 GetBallCachedVelocity()
        => RequireBallTransform().LastPhysicsLinearVelocity;

    private Vector3 GetBallCachedAngularVelocity()
        => RequireBallTransform().LastPhysicsAngularVelocity;

    private RigidBodyTransform RequireBallTransform()
        => _ballTransform
            ?? throw new InvalidOperationException(
                "MonkeyBall requires a live rigid-body transform for post-fetch physics state.");

    private IAbstractDynamicRigidBody RequireBallActor()
        => _ballBody?.RigidBody
            ?? throw new InvalidOperationException(
                "MonkeyBall requires a live native ball actor; transform-only physics is unsupported.");

    private void UpdateDesktopCamera(Vector3 ballWorldPosition, Vector3 ballVelocity, float delta)
    {
        if (_desktopCameraTransform is null)
            return;

        Vector3 horizontalVelocity = new(ballVelocity.X, 0.0f, ballVelocity.Z);
        float thresholdSquared = CameraHeadingVelocityThreshold * CameraHeadingVelocityThreshold;
        if (horizontalVelocity.LengthSquared() > thresholdSquared)
        {
            Quaternion targetHeading = CreateYawFacing(horizontalVelocity);
            _cameraHeadingRotation = InterpolateRotation(
                _cameraHeadingRotation,
                targetHeading,
                DesktopCameraYawResponse,
                delta);
        }

        _cameraYaw = ExtractYaw(_cameraHeadingRotation);
        (Vector3 cameraWorldPosition, Quaternion cameraWorldRotation) =
            CalculateDesktopCameraPose(
                ballWorldPosition,
                _cameraHeadingRotation,
                DesktopCameraOffset,
                DesktopCameraPitchDegrees);
        _desktopCameraTransform.SetWorldTranslationRotation(
            cameraWorldPosition,
            cameraWorldRotation);
        Vector3 appliedCameraPosition = _desktopCameraTransform.WorldTranslation;
        Quaternion appliedCameraRotation = _desktopCameraTransform.WorldRotation;
        MonkeyBallRuntimeValidation.RecordDesktopCamera(
            ballWorldPosition,
            ballVelocity,
            appliedCameraPosition,
            appliedCameraRotation,
            _cameraYaw,
            DesktopCameraOffset.Length());
        if (MonkeyBallRuntimeDiagnostics.Enabled)
        {
            MonkeyBallRuntimeDiagnostics.RecordDesktopCamera(
                ballWorldPosition,
                ballVelocity,
                appliedCameraPosition,
                appliedCameraRotation,
                _cameraYaw);
        }
    }

    private void ResetCameraHeading()
    {
        _cameraHeadingRotation = CreateYawFacing(-DesktopCameraOffset);
        _cameraYaw = ExtractYaw(_cameraHeadingRotation);
    }

    internal static Quaternion CalculateStageTargetRotation(
        Vector2 input,
        Quaternion cameraHeading,
        float maxTiltDegrees)
    {
        Vector3 cameraForward = HorizontalDirection(
            Vector3.Transform(Globals.Forward, cameraHeading),
            Globals.Forward);
        Vector3 cameraRight = HorizontalDirection(
            Vector3.Transform(Globals.Right, cameraHeading),
            Globals.Right);
        // Unity's camera basis is +Z forward; XRENGINE is -Z forward. Reverse both
        // axis angles so positive input still slopes the course toward the view.
        float pitch = -input.Y * maxTiltDegrees * DegreesToRadians;
        float roll = input.X * maxTiltDegrees * DegreesToRadians;
        Vector3 pitchedForward = Vector3.Transform(
            cameraForward,
            Quaternion.CreateFromAxisAngle(cameraRight, pitch));
        Vector3 rolledRight = Vector3.Transform(
            cameraRight,
            Quaternion.CreateFromAxisAngle(cameraForward, roll));
        Vector3 rotatedUp = Vector3.Normalize(Vector3.Cross(rolledRight, pitchedForward));
        return Quaternion.Normalize(XRMath.RotationBetweenVectors(Globals.Up, rotatedUp));
    }

    internal static Vector2 CameraRelativeInputToWorld(Vector2 input, float cameraYaw)
    {
        float sin = MathF.Sin(cameraYaw);
        float cos = MathF.Cos(cameraYaw);
        return new Vector2(
            input.X * cos - input.Y * sin,
            -input.X * sin - input.Y * cos);
    }

    internal static Vector3 ResolveStagePivotTranslation(Vector3 pivot, Quaternion rotation)
        => pivot - Vector3.Transform(pivot, rotation);

    internal static (Vector3 Position, Quaternion Rotation) CalculateDesktopCameraPose(
        Vector3 ballWorldPosition,
        Quaternion heading,
        Vector3 cameraOffset,
        float pitchDegrees)
    {
        float yaw = ExtractYaw(heading);
        return (
            ballWorldPosition + Vector3.Transform(cameraOffset, heading),
            Quaternion.CreateFromYawPitchRoll(
                yaw,
                pitchDegrees * DegreesToRadians,
                0.0f));
    }

    internal static Quaternion InterpolateRotation(
        Quaternion current,
        Quaternion target,
        float response,
        float delta)
    {
        float blend = response <= 0.0f
            ? 1.0f
            : Math.Clamp(response * MathF.Max(0.0f, delta), 0.0f, 1.0f);
        return Quaternion.Normalize(Quaternion.Slerp(current, target, blend));
    }

    internal static Quaternion CreateYawFacing(Vector3 direction)
    {
        Vector3 horizontal = HorizontalDirection(direction, Globals.Forward);
        float yaw = MathF.Atan2(-horizontal.X, -horizontal.Z);
        return Quaternion.CreateFromAxisAngle(Globals.Up, yaw);
    }

    internal static float ExtractYaw(Quaternion heading)
    {
        Vector3 forward = Vector3.Transform(Globals.Forward, heading);
        return MathF.Atan2(-forward.X, -forward.Z);
    }

    private static Vector3 HorizontalDirection(Vector3 direction, Vector3 fallback)
    {
        direction.Y = 0.0f;
        return direction.LengthSquared() > 1.0e-8f
            ? Vector3.Normalize(direction)
            : fallback;
    }

    private void UpdateHud(bool force)
    {
        if (_hud is null)
            return;

        _hudRefreshTimer -= Engine.Delta;
        if (!force && _hudRefreshTimer > 0.0f)
            return;
        _hudRefreshTimer = 0.1f;

        _hud.ClearShapes();

        ColorF4 stateColor = _state switch
        {
            MonkeyBallRoundState.Paused => ColorF4.Yellow,
            MonkeyBallRoundState.Falling => ColorF4.Orange,
            MonkeyBallRoundState.Won => ColorF4.Cyan,
            MonkeyBallRoundState.Lost => ColorF4.Red,
            _ => ColorF4.Green,
        };

        int seconds = Math.Clamp((int)MathF.Ceiling(_timeRemaining), 0, 99);
        AddHudNumber(_hud, seconds, 2, Vector3.Zero, ColorF4.White);
        AddHudNumber(_hud, _score, 5, new Vector3(3.2f, 0.0f, 0.0f), ColorF4.Cyan);

        for (int life = 0; life < _lives; life++)
            _hud.AddSphere(
                0.16f,
                new Vector3(1.9f + life * 0.42f, 0.25f, 0.0f),
                ColorF4.Orange,
                false);

        _hud.AddBox(
            new Vector3(0.8f, 0.08f, 0.05f),
            new Vector3(2.8f, -0.2f, 0.0f),
            stateColor,
            true);

        if (MonkeyBallRuntimeDiagnostics.Enabled)
            AddDiagnosticHudLine(_hud);
    }

    private static readonly byte[] DigitSegments =
    [
        0b0011_1111,
        0b0000_0110,
        0b0101_1011,
        0b0100_1111,
        0b0110_0110,
        0b0110_1101,
        0b0111_1101,
        0b0000_0111,
        0b0111_1111,
        0b0110_1111,
    ];

    private static void AddDiagnosticHudLine(DebugDrawComponent hud)
    {
        bool lifecycleReady =
            MonkeyBallRuntimeDiagnostics.ComponentActivations > 0 &&
            MonkeyBallRuntimeDiagnostics.BeginPlayCalls > 0;
        AddDiagnosticHudCounter(
            hud, 0, lifecycleReady ? 1 : 0, lifecycleReady);
        AddDiagnosticHudCounter(
            hud, 1, MonkeyBallRuntimeDiagnostics.InputRegistrations, MonkeyBallRuntimeDiagnostics.InputRegistrations > 0);
        AddDiagnosticHudCounter(
            hud, 2, MonkeyBallRuntimeDiagnostics.PrePhysicsTicks, MonkeyBallRuntimeDiagnostics.PrePhysicsTicks > 0);
        AddDiagnosticHudCounter(
            hud, 3, MonkeyBallRuntimeDiagnostics.PhysicsSteps, MonkeyBallRuntimeDiagnostics.PhysicsSteps > 0);
        AddDiagnosticHudCounter(
            hud, 4, MonkeyBallRuntimeDiagnostics.ShadowPasses, MonkeyBallRuntimeDiagnostics.ShadowPasses > 0);
    }

    private static void AddDiagnosticHudCounter(
        DebugDrawComponent hud,
        int index,
        long value,
        bool ready)
    {
        Vector3 origin = new(index * 1.6f, -1.45f, 0.0f);
        ColorF4 color = ready ? ColorF4.Green : ColorF4.Red;
        hud.AddBox(
            new Vector3(0.68f, 0.04f, 0.04f),
            origin + new Vector3(0.32f, -0.12f, 0.0f),
            color,
            true);
        AddHudNumber(hud, (int)(value % 100), 2, origin, color);
    }

    private static void AddHudNumber(
        DebugDrawComponent hud,
        int value,
        int digits,
        Vector3 origin,
        ColorF4 color)
    {
        int divisor = 1;
        for (int i = 1; i < digits; i++)
            divisor *= 10;

        int clamped = Math.Clamp(value, 0, divisor * 10 - 1);
        for (int digitIndex = 0; digitIndex < digits; digitIndex++)
        {
            int digit = clamped / divisor;
            clamped %= divisor;
            divisor = Math.Max(1, divisor / 10);
            AddHudDigit(
                hud,
                digit,
                origin + new Vector3(digitIndex * 0.72f, 0.0f, 0.0f),
                color);
        }
    }

    private static void AddHudDigit(
        DebugDrawComponent hud,
        int digit,
        Vector3 origin,
        ColorF4 color)
    {
        const float width = 0.5f;
        const float height = 1.0f;
        const float middle = height * 0.5f;
        byte segments = DigitSegments[Math.Clamp(digit, 0, 9)];

        AddHudSegment(hud, segments, 0, origin + new Vector3(0.0f, height, 0.0f), origin + new Vector3(width, height, 0.0f), color);
        AddHudSegment(hud, segments, 1, origin + new Vector3(width, height, 0.0f), origin + new Vector3(width, middle, 0.0f), color);
        AddHudSegment(hud, segments, 2, origin + new Vector3(width, middle, 0.0f), origin + new Vector3(width, 0.0f, 0.0f), color);
        AddHudSegment(hud, segments, 3, origin, origin + new Vector3(width, 0.0f, 0.0f), color);
        AddHudSegment(hud, segments, 4, origin + new Vector3(0.0f, middle, 0.0f), origin, color);
        AddHudSegment(hud, segments, 5, origin + new Vector3(0.0f, height, 0.0f), origin + new Vector3(0.0f, middle, 0.0f), color);
        AddHudSegment(hud, segments, 6, origin + new Vector3(0.0f, middle, 0.0f), origin + new Vector3(width, middle, 0.0f), color);
    }

    private static void AddHudSegment(
        DebugDrawComponent hud,
        byte segments,
        int segmentIndex,
        Vector3 start,
        Vector3 end,
        ColorF4 color)
    {
        if ((segments & (1 << segmentIndex)) != 0)
            hud.AddLine(start, end, color);
    }
}
