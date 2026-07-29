using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace MonkeyBallVR;

/// <summary>
/// Coordinates the authored MonkeyBall rigid bodies, stage controls, camera,
/// round state, and HUD.
/// </summary>
public sealed class MonkeyBallGameComponent : XRComponent
{
    private const float DegreesToRadians = MathF.PI / 180.0f;

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
    private int _initialLives = 3;
    private float _maxBallSpeed = 12.0f;
    private float _fallThresholdY = -3.5f;
    private float _fallResetDelaySeconds = 0.75f;
    private Vector3 _desktopCameraOffset = new(0.0f, 2.5f, 5.5f);
    private float _desktopCameraPitchDegrees = -14.0f;
    private float _desktopCameraYawResponse = 4.5f;
    private float _cameraHeadingVelocityThreshold = 0.15f;

    private DynamicRigidBodyComponent? _courseBody;
    private DynamicRigidBodyComponent? _ballBody;
    private RigidBodyTransform? _courseTransform;
    private RigidBodyTransform? _ballTransform;
    private Transform? _desktopCameraTransform;
    private DebugDrawComponent? _hud;
    private MonkeyBallPawnComponent? _pawn;
    private Vector2 _tilt;
    private float _cameraYaw;
    private float _stateTimer;
    private float _timeRemaining;
    private float _hudRefreshTimer;
    private int _lives;
    private int _score;
    private bool _pendingBallReset;
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
        => _tilt = Vector2.Clamp(tilt, new Vector2(-1.0f), new Vector2(1.0f));

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
        base.OnBeginPlay();
        ResolveSceneReferences();
        _pawn!.PossessByLocalPlayer(ELocalPlayerIndex.One);
        _lives = InitialLives;
        _score = 0;
        ResetBall(resetTimer: true);
        UpdateDesktopCamera(GetBallRenderPosition(), Vector3.Zero, 0.0f);
        UpdateHud(force: true);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.PrePhysics, ETickOrder.Logic, PrePhysicsTick);
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, Tick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.PrePhysics, ETickOrder.Logic, PrePhysicsTick);
        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, Tick);
        _tilt = Vector2.Zero;
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

        CameraComponent camera = cameraNode.GetComponent<CameraComponent>()
            ?? throw new InvalidOperationException(
                $"MonkeyBall camera node '{DesktopCameraNodeName}' has no {nameof(CameraComponent)}.");
        ConfigureDesktopCamera(camera);
        _pawn.CameraComponent = camera;
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

    private void PrePhysicsTick()
    {
        if (_courseBody is null || _courseTransform is null)
            return;

        Vector2 input = _state == MonkeyBallRoundState.Playing ? _tilt : Vector2.Zero;
        Vector2 worldTilt = CameraRelativeInputToWorld(input, _cameraYaw);
        float pitch = -worldTilt.Y * MaxTiltDegrees * DegreesToRadians;
        float roll = -worldTilt.X * MaxTiltDegrees * DegreesToRadians;
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0.0f, pitch, roll);
        Vector3 ballPosition = GetBallPhysicsPosition();
        Vector3 pivot = new(ballPosition.X, 0.0f, ballPosition.Z);
        Vector3 translation = ResolveStagePivotTranslation(pivot, rotation);
        var target = (translation, rotation);

        _courseBody.KinematicTarget = target;
        if (_courseBody.RigidBody is not null)
            _courseBody.RigidBody.KinematicTarget = target;
        else
            _courseTransform.SetPositionAndRotation(translation, rotation);
    }

    private void Tick()
    {
        TryApplyPendingBallReset();

        float delta = Math.Clamp(Engine.Delta, 0.0f, 0.1f);
        Vector3 position = GetBallPhysicsPosition();
        Vector3 velocity = GetBallVelocity();

        if (_state != MonkeyBallRoundState.Paused)
            UpdateRoundState(position, velocity, delta);

        UpdateDesktopCamera(GetBallRenderPosition(), velocity, delta);
        UpdateHud(force: false);
    }

    private void UpdateRoundState(Vector3 position, Vector3 velocity, float delta)
    {
        switch (_state)
        {
            case MonkeyBallRoundState.Playing:
                _timeRemaining = MathF.Max(0.0f, _timeRemaining - delta);
                ClampBallSpeed(velocity);
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

    private void ClampBallSpeed(Vector3 velocity)
    {
        if (_ballBody is null)
            return;

        float maxSpeedSquared = MaxBallSpeed * MaxBallSpeed;
        float speedSquared = velocity.LengthSquared();
        if (speedSquared <= maxSpeedSquared)
            return;

        _ballBody.LinearVelocity = velocity * (MaxBallSpeed / MathF.Sqrt(speedSquared));
    }

    private void ResetBall(bool resetTimer)
    {
        _state = MonkeyBallRoundState.Playing;
        _stateTimer = 0.0f;
        _cameraYaw = 0.0f;
        if (resetTimer)
            _timeRemaining = RoundDurationSeconds;

        _pendingBallReset = true;
        TryApplyPendingBallReset();
    }

    private void TryApplyPendingBallReset()
    {
        if (!_pendingBallReset || _ballBody is null || _ballTransform is null)
            return;

        Vector3 position = new(StartPosition.X, BallRadius + 0.08f, StartPosition.Y);
        _ballTransform.SetPositionAndRotation(position, Quaternion.Identity);
        _ballBody.SimulationEnabled = true;
        _ballBody.LinearVelocity = Vector3.Zero;
        _ballBody.AngularVelocity = Vector3.Zero;

        IAbstractDynamicRigidBody? rigidBody = _ballBody.RigidBody;
        if (rigidBody is null)
            return;

        rigidBody.SetTransform(position, Quaternion.Identity);
        rigidBody.SetLinearVelocity(Vector3.Zero);
        rigidBody.SetAngularVelocity(Vector3.Zero);
        rigidBody.WakeUp();
        _pendingBallReset = false;
    }

    private void SetBallSimulationEnabled(bool enabled)
    {
        if (_ballBody is not null)
            _ballBody.SimulationEnabled = enabled;
    }

    private Vector3 GetBallPhysicsPosition()
        => _ballBody?.RigidBody?.Transform.position
            ?? _ballTransform?.Position
            ?? new Vector3(StartPosition.X, BallRadius, StartPosition.Y);

    private Vector3 GetBallRenderPosition()
        => _ballTransform?.WorldTranslation ?? GetBallPhysicsPosition();

    private Vector3 GetBallVelocity()
        => _ballBody?.RigidBody?.LinearVelocity
            ?? _ballBody?.LinearVelocity
            ?? Vector3.Zero;

    private void UpdateDesktopCamera(Vector3 ballWorldPosition, Vector3 ballVelocity, float delta)
    {
        if (_desktopCameraTransform is null)
            return;

        Vector2 horizontalVelocity = new(ballVelocity.X, ballVelocity.Z);
        float thresholdSquared = CameraHeadingVelocityThreshold * CameraHeadingVelocityThreshold;
        if (horizontalVelocity.LengthSquared() > thresholdSquared)
        {
            float targetYaw = MathF.Atan2(-horizontalVelocity.X, -horizontalVelocity.Y);
            _cameraYaw = InterpolateAngle(
                _cameraYaw,
                targetYaw,
                DesktopCameraYawResponse,
                delta);
        }

        Quaternion yawRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _cameraYaw);
        Vector3 cameraWorldPosition =
            ballWorldPosition + Vector3.Transform(DesktopCameraOffset, yawRotation);
        Quaternion cameraWorldRotation = Quaternion.CreateFromYawPitchRoll(
            _cameraYaw,
            DesktopCameraPitchDegrees * DegreesToRadians,
            0.0f);
        _desktopCameraTransform.SetWorldTranslationRotation(
            cameraWorldPosition,
            cameraWorldRotation);
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

    internal static float InterpolateAngle(float current, float target, float response, float delta)
    {
        float difference = MathF.Atan2(
            MathF.Sin(target - current),
            MathF.Cos(target - current));
        float blend = response <= 0.0f
            ? 1.0f
            : 1.0f - MathF.Exp(-response * MathF.Max(0.0f, delta));
        return current + difference * blend;
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
