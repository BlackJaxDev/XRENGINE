using System.Diagnostics;
using System.Numerics;
using XREngine;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace MonkeyBallVR;

/// <summary>
/// Deterministic acceptance gate used by the published runtime smoke and the
/// opt-in diagnostics validator. It observes real cooked runtime state and
/// never substitutes simulation or rendering behavior.
/// </summary>
internal static class MonkeyBallRuntimeValidation
{
    private const string ValidateEnvironmentVariable = "XRE_MONKEYBALL_DIAGNOSTICS_VALIDATE";
    private const int MinimumValidationTicks = 300;
    private static readonly bool EnvironmentValidationRequested =
        IsEnabledEnvironmentValue(Environment.GetEnvironmentVariable(ValidateEnvironmentVariable));
    private static readonly object Sync = new();
    private static int _runtimeSmokeRequested;
    private static int _started;
    private static int _completed;
    private static int _passed;
    private static long _startTimestamp;
    private static long _componentActivations;
    private static long _beginPlayCalls;
    private static long _prePhysicsTicks;
    private static long _normalTicks;
    private static long _physicsSteps;
    private static string? _failure;
    private static bool _inputRegistrationReady;
    private static bool _inputBindingsReady;
    private static bool _keyboardBindingReady;
    private static bool _gamepadBindingReady;
    private static bool _vrBindingsAttempted;
    private static bool _possessionReady;
    private static bool _physicsRuntimeReady;
    private static bool _physicsCadenceReady;
    private static bool _ballInterpolationReady;
    private static bool _courseTargetChanged;
    private static bool _pivotTranslationReady;
    private static bool _cameraRelativeTiltReady;
    private static bool _cameraFollowReady;
    private static bool _cameraUprightReady;
    private static bool _cameraYawReady;
    private static bool _cameraRenderFollowReady;
    private static bool _cameraRenderUprightReady;
    private static bool _ballBaselineCaptured;
    private static bool _ballStateChanged;
    private static bool _shadowReady;
    private static Vector3 _ballBaselinePosition;
    private static Vector3 _ballBaselineVelocity;

    public static bool Enabled =>
        EnvironmentValidationRequested ||
        Volatile.Read(ref _runtimeSmokeRequested) != 0;

    public static void ConfigureRuntimeSmoke()
    {
        Interlocked.Exchange(ref _runtimeSmokeRequested, 1);
        EnsureStarted();
        Log("runtime-smoke-configured", "profile=desktop scriptedTilt=true");
    }

    public static void CompleteRuntimeSmoke()
    {
        if (Volatile.Read(ref _passed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
        {
            _failure = BuildFailure("engine loop exited before validation completed");
            Environment.ExitCode = 1;
        }

        throw new InvalidOperationException(
            $"MonkeyBall runtime smoke failed: {_failure ?? "unknown failure"}");
    }

    public static bool TryGetScriptedTilt(out Vector2 tilt)
    {
        if (!Enabled)
        {
            tilt = Vector2.Zero;
            return false;
        }

        EnsureStarted();
        long tick = Interlocked.Read(ref _prePhysicsTicks);
        tilt = tick switch
        {
            >= 30 and < 150 => new Vector2(1.0f, 0.0f),
            >= 150 and < 270 => new Vector2(0.0f, 1.0f),
            _ => Vector2.Zero,
        };
        return true;
    }

    public static void RecordComponentActivated()
    {
        if (!Enabled)
            return;

        EnsureStarted();
        Interlocked.Increment(ref _componentActivations);
    }

    public static void RecordBeginPlay()
    {
        if (!Enabled)
            return;

        EnsureStarted();
        Interlocked.Increment(ref _beginPlayCalls);
    }

    public static void RecordInputRegistration(string inputType, bool unregister)
    {
        if (!Enabled || unregister)
            return;

        lock (Sync)
            _inputRegistrationReady = !string.Equals(inputType, "null", StringComparison.Ordinal);
    }

    public static void RecordInputBindings(bool unregister)
    {
        if (!Enabled || unregister)
            return;

        lock (Sync)
        {
            _inputBindingsReady = true;
            _vrBindingsAttempted = true;
        }
    }

    public static void RecordInputDevices(
        bool keyboardBound,
        bool gamepadBound,
        bool unregister)
    {
        if (!Enabled || unregister)
            return;

        lock (Sync)
        {
            _keyboardBindingReady |= keyboardBound;
            _gamepadBindingReady |= gamepadBound;
        }
    }

    public static void RecordPossession(
        bool isLocal,
        bool controlsAuthoredPawn,
        string inputType,
        string viewportType,
        string cameraType)
    {
        if (!Enabled)
            return;

        lock (Sync)
        {
            _possessionReady |=
                isLocal &&
                controlsAuthoredPawn &&
                !string.Equals(inputType, "null", StringComparison.Ordinal) &&
                !string.Equals(viewportType, "null", StringComparison.Ordinal) &&
                !string.Equals(cameraType, "null", StringComparison.Ordinal);
        }
    }

    public static void RecordPhysicsRuntime(
        string sceneType,
        float authoredTimestep,
        float engineFixedDelta,
        RigidBodyTransform.EInterpolationMode ballInterpolationMode,
        bool courseActive,
        bool ballActive,
        string courseActorType,
        string ballActorType,
        bool courseInScene,
        bool ballInScene,
        PhysicsRigidBodyFlags courseFlags,
        int courseColliderCount,
        int ballColliderCount,
        bool ballGravityEnabled,
        bool ballSimulationEnabled)
    {
        if (!Enabled)
            return;

        const float requiredFixedDelta = 1.0f / 120.0f;
        const float tolerance = 1.0e-6f;
        lock (Sync)
        {
            _physicsCadenceReady =
                MathF.Abs(authoredTimestep - requiredFixedDelta) <= tolerance &&
                MathF.Abs(engineFixedDelta - requiredFixedDelta) <= tolerance;
            _ballInterpolationReady =
                ballInterpolationMode == RigidBodyTransform.EInterpolationMode.Interpolate;
            _physicsRuntimeReady =
                !string.Equals(sceneType, "null", StringComparison.Ordinal) &&
                courseActive &&
                ballActive &&
                !string.Equals(courseActorType, "null", StringComparison.Ordinal) &&
                !string.Equals(ballActorType, "null", StringComparison.Ordinal) &&
                courseInScene &&
                ballInScene &&
                courseFlags.HasFlag(PhysicsRigidBodyFlags.Kinematic) &&
                courseColliderCount > 0 &&
                ballColliderCount > 0 &&
                ballGravityEnabled &&
                ballSimulationEnabled &&
                _physicsCadenceReady &&
                _ballInterpolationReady;
        }
    }

    public static void RecordPhysicsStep()
    {
        if (Enabled)
            Interlocked.Increment(ref _physicsSteps);
    }

    public static void RecordPrePhysics(
        Vector2 input,
        Vector2 worldTilt,
        float cameraYaw,
        Vector3 pivot,
        Vector3 targetPosition,
        Quaternion appliedRotation,
        bool courseActorExists,
        bool ballActorExists)
    {
        if (!Enabled)
            return;

        Interlocked.Increment(ref _prePhysicsTicks);
        Vector3 expectedTranslation = pivot - Vector3.Transform(pivot, appliedRotation);
        bool pivotReady = Vector3.DistanceSquared(expectedTranslation, targetPosition) <= 1.0e-6f;
        bool rotationChanged =
            input.LengthSquared() > 0.25f &&
            MathF.Abs(Quaternion.Dot(appliedRotation, Quaternion.Identity)) < 0.99999f;
        Vector2 zeroYawWorldTilt = new(input.X, -input.Y);
        bool cameraRelativeTiltReady =
            input.LengthSquared() > 0.25f &&
            MathF.Abs(cameraYaw) > 0.05f &&
            Vector2.DistanceSquared(worldTilt, zeroYawWorldTilt) > 0.0025f;

        lock (Sync)
        {
            _pivotTranslationReady |= pivotReady && courseActorExists && ballActorExists;
            _courseTargetChanged |= rotationChanged && courseActorExists;
            _cameraRelativeTiltReady |= cameraRelativeTiltReady && courseActorExists;
        }
    }

    public static void RecordDesktopCamera(
        Vector3 ballPosition,
        Vector3 ballVelocity,
        Vector3 cameraPosition,
        Quaternion cameraRotation,
        float cameraYaw,
        float expectedOffsetLength)
    {
        if (!Enabled)
            return;

        float actualOffsetLength = Vector3.Distance(ballPosition, cameraPosition);
        Vector3 cameraRight = Vector3.Transform(Globals.Right, cameraRotation);
        Vector3 horizontalVelocity = new(ballVelocity.X, 0.0f, ballVelocity.Z);
        Vector3 horizontalForward = Vector3.Transform(Globals.Forward, cameraRotation);
        horizontalForward.Y = 0.0f;

        bool followsBall =
            MathF.Abs(actualOffsetLength - expectedOffsetLength) <= 0.001f;
        bool remainsUpright =
            MathF.Abs(Vector3.Dot(cameraRight, Globals.Up)) <= 0.001f;
        bool facesVelocity =
            horizontalVelocity.LengthSquared() > 0.04f &&
            horizontalForward.LengthSquared() > 1.0e-8f &&
            MathF.Abs(cameraYaw) > 0.05f &&
            Vector3.Dot(
                Vector3.Normalize(horizontalForward),
                Vector3.Normalize(horizontalVelocity)) > 0.65f;

        lock (Sync)
        {
            _cameraFollowReady |= followsBall;
            _cameraUprightReady |= remainsUpright;
            _cameraYawReady |= facesVelocity;
        }
    }

    public static void RecordDesktopCameraPresentation(
        Vector3 ballRenderPosition,
        Vector3 cameraRenderPosition,
        Quaternion cameraRenderRotation,
        float expectedOffsetLength)
    {
        if (!Enabled)
            return;

        float actualOffsetLength =
            Vector3.Distance(ballRenderPosition, cameraRenderPosition);
        Vector3 cameraRight =
            Vector3.Transform(Globals.Right, cameraRenderRotation);
        bool followsBall =
            MathF.Abs(actualOffsetLength - expectedOffsetLength) <= 0.001f;
        bool remainsUpright =
            MathF.Abs(Vector3.Dot(cameraRight, Globals.Up)) <= 0.001f;

        lock (Sync)
        {
            _cameraRenderFollowReady |= followsBall;
            _cameraRenderUprightReady |= remainsUpright;
        }
    }

    public static void RecordNormalTick(
        Vector3 ballPosition,
        Vector3 ballVelocity,
        bool physicsEnabled,
        string physicsSceneType,
        bool courseActorInScene,
        bool ballActorInScene)
    {
        if (!Enabled)
            return;

        long count = Interlocked.Increment(ref _normalTicks);
        lock (Sync)
        {
            _physicsRuntimeReady &=
                physicsEnabled &&
                !string.Equals(physicsSceneType, "null", StringComparison.Ordinal) &&
                courseActorInScene &&
                ballActorInScene;

            if (!_ballBaselineCaptured && Interlocked.Read(ref _prePhysicsTicks) >= 30)
            {
                _ballBaselinePosition = ballPosition;
                _ballBaselineVelocity = ballVelocity;
                _ballBaselineCaptured = true;
            }
            else if (_ballBaselineCaptured && _courseTargetChanged)
            {
                _ballStateChanged |=
                    Vector3.DistanceSquared(_ballBaselinePosition, ballPosition) > 0.0025f ||
                    Vector3.DistanceSquared(_ballBaselineVelocity, ballVelocity) > 0.0025f;
            }
        }

        if (count >= MinimumValidationTicks)
            TryFinishSuccessfully();
    }

    public static void RecordDirectionalShadow(
        bool active,
        string lightType,
        bool castsShadows,
        bool usesAtlas,
        bool cascaded,
        uint width,
        uint height,
        bool mapAllocated,
        bool receiverTexture,
        bool shadowCamera,
        string desktopCameraMode,
        int casterCount,
        long renderRequestCount,
        long renderPassCount)
    {
        if (!Enabled)
            return;

        lock (Sync)
        {
            _shadowReady |=
                active &&
                string.Equals(lightType, "Dynamic", StringComparison.Ordinal) &&
                castsShadows &&
                !usesAtlas &&
                !cascaded &&
                width == 2048 &&
                height == 2048 &&
                mapAllocated &&
                receiverTexture &&
                shadowCamera &&
                string.Equals(desktopCameraMode, "NonCascaded", StringComparison.Ordinal) &&
                casterCount >= 7 &&
                renderRequestCount > 0 &&
                renderPassCount > 0;
        }
    }

    private static void EnsureStarted()
    {
        if (!Enabled || Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _startTimestamp = Stopwatch.GetTimestamp();
        Thread watchdog = new(Watchdog)
        {
            IsBackground = true,
            Name = "MonkeyBall runtime validation watchdog",
        };
        watchdog.Start();
    }

    private static void Watchdog()
    {
        Thread.Sleep(TimeSpan.FromSeconds(45.0));
        if (Volatile.Read(ref _completed) == 0)
            Finish(false, BuildFailure("45-second watchdog expired"));
    }

    private static void TryFinishSuccessfully()
    {
        bool passed;
        lock (Sync)
        {
            long normalTicks = Interlocked.Read(ref _normalTicks);
            long physicsSteps = Interlocked.Read(ref _physicsSteps);
            bool observedCadenceReady =
                physicsSteps * 4L >= normalTicks * 5L;
            passed =
                Interlocked.Read(ref _componentActivations) > 0 &&
                Interlocked.Read(ref _beginPlayCalls) > 0 &&
                Interlocked.Read(ref _prePhysicsTicks) >= MinimumValidationTicks &&
                normalTicks >= MinimumValidationTicks &&
                physicsSteps > 0 &&
                observedCadenceReady &&
                _inputRegistrationReady &&
                _inputBindingsReady &&
                _keyboardBindingReady &&
                _gamepadBindingReady &&
                _vrBindingsAttempted &&
                _possessionReady &&
                _physicsRuntimeReady &&
                _courseTargetChanged &&
                _pivotTranslationReady &&
                _cameraRelativeTiltReady &&
                _cameraFollowReady &&
                _cameraUprightReady &&
                _cameraYawReady &&
                _cameraRenderFollowReady &&
                _cameraRenderUprightReady &&
                _ballStateChanged &&
                _shadowReady;
        }

        if (!passed)
            return;

        double elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        Finish(
            true,
            $"ticks={Interlocked.Read(ref _normalTicks)} " +
            $"physicsSteps={Interlocked.Read(ref _physicsSteps)} " +
            $"fixedHz=120 elapsedSeconds={elapsed:F3}");
    }

    private static void Finish(bool passed, string detail)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;

        if (passed)
            Interlocked.Exchange(ref _passed, 1);
        else
        {
            _failure = detail;
            Environment.ExitCode = 1;
        }

        Log(passed ? "runtime-validation-passed" : "runtime-validation-failed", detail);
        Engine.ShutDown();
    }

    private static string BuildFailure(string reason)
    {
        lock (Sync)
        {
            return
                $"reason={Normalize(reason)} activation={Interlocked.Read(ref _componentActivations)} " +
                $"beginPlay={Interlocked.Read(ref _beginPlayCalls)} " +
                $"prePhysics={Interlocked.Read(ref _prePhysicsTicks)} " +
                $"normal={Interlocked.Read(ref _normalTicks)} " +
                $"physicsSteps={Interlocked.Read(ref _physicsSteps)} " +
                $"inputRegistration={_inputRegistrationReady} inputBindings={_inputBindingsReady} " +
                $"keyboard={_keyboardBindingReady} gamepad={_gamepadBindingReady} " +
                $"vrBindings={_vrBindingsAttempted} possession={_possessionReady} " +
                $"physicsRuntime={_physicsRuntimeReady} courseTargetChanged={_courseTargetChanged} " +
                $"physicsCadence={_physicsCadenceReady} " +
                $"ballInterpolation={_ballInterpolationReady} " +
                $"pivot={_pivotTranslationReady} cameraRelativeTilt={_cameraRelativeTiltReady} " +
                $"cameraFollow={_cameraFollowReady} cameraUpright={_cameraUprightReady} " +
                $"cameraYaw={_cameraYawReady} ballChanged={_ballStateChanged} shadow={_shadowReady} " +
                $"cameraRenderFollow={_cameraRenderFollowReady} " +
                $"cameraRenderUpright={_cameraRenderUprightReady}";
        }
    }

    private static void Log(string eventName, string detail)
    {
        Console.WriteLine($"MonkeyBall runtime validation event={eventName} {detail}");
        MonkeyBallRuntimeDiagnostics.RecordEvent(eventName, detail);
    }

    private static string Normalize(string value)
        => value.Replace(' ', '_').Replace(',', '+').Replace('\r', '_').Replace('\n', '_');

    private static bool IsEnabledEnvironmentValue(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
