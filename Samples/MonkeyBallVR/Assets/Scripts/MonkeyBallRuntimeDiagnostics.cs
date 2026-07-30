using System.Globalization;
using System.Numerics;
using System.Text;
using XREngine.Components.Physics;
using XREngine.Scene.Physics;

namespace MonkeyBallVR;

/// <summary>
/// Opt-in, NativeAOT-safe runtime evidence for the cooked MonkeyBall game.
/// Set <c>XRE_MONKEYBALL_DIAGNOSTICS_PATH</c> to an absolute output path.
/// </summary>
internal static class MonkeyBallRuntimeDiagnostics
{
    private const string DiagnosticsPathEnvironmentVariable = "XRE_MONKEYBALL_DIAGNOSTICS_PATH";
    private static readonly object Sync = new();
    private static readonly string? OutputPath = ResolveOutputPath();
    private static long _componentActivations;
    private static long _beginPlayCalls;
    private static long _prePhysicsTicks;
    private static long _normalTicks;
    private static long _physicsSteps;
    private static long _inputRegistrations;
    private static long _inputCallbacks;
    private static long _shadowPasses;
    private static long _shadowSamples;
    private static long _cameraSamples;
    private static long _cameraPresentationSamples;

    public static bool Enabled => OutputPath is not null;

    public static long ComponentActivations => Interlocked.Read(ref _componentActivations);
    public static long BeginPlayCalls => Interlocked.Read(ref _beginPlayCalls);
    public static long PrePhysicsTicks => Interlocked.Read(ref _prePhysicsTicks);
    public static long NormalTicks => Interlocked.Read(ref _normalTicks);
    public static long PhysicsSteps => Interlocked.Read(ref _physicsSteps);
    public static long InputRegistrations => Interlocked.Read(ref _inputRegistrations);
    public static long InputCallbacks => Interlocked.Read(ref _inputCallbacks);
    public static long ShadowPasses => Interlocked.Read(ref _shadowPasses);

    public static void RecordComponentActivated()
        => Interlocked.Increment(ref _componentActivations);

    public static void RecordBeginPlay()
        => Interlocked.Increment(ref _beginPlayCalls);

    public static void RecordEvent(string name, string? detail = null)
    {
        if (!Enabled)
            return;

        WriteLine(
            string.IsNullOrWhiteSpace(detail)
                ? $"event={name}"
                : $"event={name} {detail}");
    }

    public static void RecordInputRegistration(string inputType, bool unregister)
    {
        long count = Interlocked.Increment(ref _inputRegistrations);
        RecordEvent(
            "input-registration",
            $"count={count} inputType={Sanitize(inputType)} unregister={unregister}");
    }

    public static void RecordInputBindingSet(bool unregister)
        => RecordEvent(
            "input-bindings",
            $"unregister={unregister} keyboardStateBindings=8 keyboardEvents=2 " +
            "gamepadAxes=2 gamepadButtons=2 vrVector2Actions=1 vrBoolActions=2 " +
            "vrActionSet=Global");

    public static void RecordPossession(
        string controllerType,
        bool isLocal,
        string localPlayerIndex,
        bool controlsAuthoredPawn,
        string inputType,
        string viewportType,
        string cameraType)
        => RecordEvent(
            "possession",
            $"controller={Sanitize(controllerType)} isLocal={isLocal} " +
            $"localPlayer={Sanitize(localPlayerIndex)} authoredPawn={controlsAuthoredPawn} " +
            $"input={Sanitize(inputType)} viewport={Sanitize(viewportType)} " +
            $"camera={Sanitize(cameraType)}");

    public static void RecordAnalogInput(string binding, Vector2 value)
    {
        long count = Interlocked.Increment(ref _inputCallbacks);
        RecordEvent(
            "input-analog",
            $"count={count} binding={Sanitize(binding)} value={Format(value)}");
    }

    public static void RecordInputCallback(string binding, bool pressed)
    {
        long count = Interlocked.Increment(ref _inputCallbacks);
        RecordEvent(
            "input-callback",
            $"count={count} binding={Sanitize(binding)} pressed={pressed}");
    }

    public static void RecordTilt(Vector2 tilt)
        => RecordEvent("tilt", $"value={Format(tilt)}");

    public static void RecordBallReset(
        Vector3 requestedPosition,
        Vector3 actorPosition,
        Quaternion actorRotation,
        Vector3 linearVelocity,
        Vector3 angularVelocity,
        bool sleeping,
        Vector3 interpolationPosition,
        Vector3 interpolationLinearVelocity,
        Vector3 interpolationAngularVelocity)
        => RecordEvent(
            "ball-reset",
            $"requestedPosition={Format(requestedPosition)} " +
            $"actorPosition={Format(actorPosition)} actorRotation={Format(actorRotation)} " +
            $"linearVelocity={Format(linearVelocity)} angularVelocity={Format(angularVelocity)} " +
            $"sleeping={sleeping} interpolationPosition={Format(interpolationPosition)} " +
            $"interpolationLinearVelocity={Format(interpolationLinearVelocity)} " +
            $"interpolationAngularVelocity={Format(interpolationAngularVelocity)}");

    public static void RecordPhysicsStep()
    {
        long count = Interlocked.Increment(ref _physicsSteps);
        if (count == 1 || count % 90 == 0)
            RecordEvent("physics-step", $"count={count}");
    }

    public static void RecordDirectionalShadow(
        string phase,
        long activationCount,
        bool active,
        string lightType,
        bool castsShadows,
        bool usesAtlas,
        bool cascaded,
        uint width,
        uint height,
        Vector3 scale,
        string storageFormat,
        string encoding,
        bool mapAllocated,
        bool receiverTexture,
        bool shadowCamera,
        float cameraNear,
        float cameraFar,
        string desktopCameraMode,
        int casterCount,
        long renderRequestCount,
        long renderPassCount)
    {
        Interlocked.Exchange(ref _shadowPasses, renderPassCount);
        long sample = Interlocked.Increment(ref _shadowSamples);
        if (!string.Equals(phase, "resolved", StringComparison.Ordinal) &&
            sample != 1 &&
            sample % 90 != 0)
        {
            return;
        }

        RecordEvent(
            "directional-shadow",
            $"phase={Sanitize(phase)} sample={sample} activations={activationCount} " +
            $"active={active} type={Sanitize(lightType)} castsShadows={castsShadows} " +
            $"useAtlas={usesAtlas} cascaded={cascaded} resolution={width}x{height} " +
            $"scale={Format(scale)} storage={Sanitize(storageFormat)} " +
            $"encoding={Sanitize(encoding)} mapAllocated={mapAllocated} " +
            $"receiverTexture={receiverTexture} shadowCamera={shadowCamera} " +
            $"cameraNear={Format(cameraNear)} cameraFar={Format(cameraFar)} " +
            $"desktopCameraMode={Sanitize(desktopCameraMode)} casters={casterCount} " +
            $"renderRequests={renderRequestCount} renderPasses={renderPassCount}");
    }

    public static void RecordPhysicsRuntime(
        string sceneType,
        Vector3 gravity,
        float timestep,
        int substeps,
        float engineFixedDelta,
        string ballInterpolationMode,
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
        float engineFixedHz =
            engineFixedDelta > 0.0f ? 1.0f / engineFixedDelta : 0.0f;
        RecordEvent(
            "physics-runtime",
            $"scene={Sanitize(sceneType)} gravity={Format(gravity)} timestep={Format(timestep)} " +
            $"substeps={substeps} engineFixedDelta={Format(engineFixedDelta)} " +
            $"engineFixedHz={Format(engineFixedHz)} " +
            $"ballInterpolation={Sanitize(ballInterpolationMode)} " +
            $"courseActive={courseActive} ballActive={ballActive} " +
            $"courseActorType={Sanitize(courseActorType)} ballActorType={Sanitize(ballActorType)} " +
            $"courseInScene={courseInScene} ballInScene={ballInScene} " +
            $"courseFlags={Sanitize(courseFlags.ToString())} courseColliders={courseColliderCount} " +
            $"ballColliders={ballColliderCount} ballGravity={ballGravityEnabled} " +
            $"ballSimulation={ballSimulationEnabled}");
    }

    public static void RecordPrePhysics(
        Vector2 input,
        Vector2 worldTilt,
        float cameraYaw,
        Vector3 pivot,
        Vector3 targetPosition,
        Quaternion stageTargetRotation,
        Quaternion appliedRotation,
        bool courseActorExists,
        bool ballActorExists)
    {
        long count = Interlocked.Increment(ref _prePhysicsTicks);
        if (count != 1 && count % 90 != 0)
            return;

        RecordEvent(
            "pre-physics",
            $"count={count} input={Format(input)} worldTilt={Format(worldTilt)} " +
            $"cameraYaw={Format(cameraYaw)} " +
            $"pivot={Format(pivot)} targetPosition={Format(targetPosition)} " +
            $"stageTargetRotation={Format(stageTargetRotation)} " +
            $"appliedRotation={Format(appliedRotation)} " +
            $"courseActor={courseActorExists} ballActor={ballActorExists}");
    }

    public static void RecordNormalTick(
        Vector3 ballPosition,
        Vector3 ballVelocity,
        Vector3 ballAngularVelocity,
        bool ballSleeping,
        bool physicsEnabled,
        string physicsSceneType,
        bool courseActorInScene,
        bool ballActorInScene)
    {
        long count = Interlocked.Increment(ref _normalTicks);
        if (count != 1 && count % 90 != 0)
            return;

        RecordEvent(
            "normal-tick",
            $"count={count} ballPosition={Format(ballPosition)} ballVelocity={Format(ballVelocity)} " +
            $"ballAngularVelocity={Format(ballAngularVelocity)} ballSleeping={ballSleeping} " +
            $"physicsEnabled={physicsEnabled} physicsScene={Sanitize(physicsSceneType)} " +
            $"courseInScene={courseActorInScene} ballInScene={ballActorInScene} " +
            $"prePhysicsTicks={PrePhysicsTicks} physicsSteps={PhysicsSteps} " +
            $"inputRegistrations={InputRegistrations} " +
            $"inputCallbacks={InputCallbacks}");
    }

    public static void RecordDesktopCamera(
        Vector3 ballPosition,
        Vector3 ballVelocity,
        Vector3 cameraPosition,
        Quaternion cameraRotation,
        float cameraYaw)
    {
        long sample = Interlocked.Increment(ref _cameraSamples);
        if (sample != 1 && sample % 90 != 0)
            return;

        Vector3 cameraRight = Vector3.Transform(Vector3.UnitX, cameraRotation);
        Vector3 cameraForward = Vector3.Transform(-Vector3.UnitZ, cameraRotation);
        Vector3 horizontalVelocity = new(ballVelocity.X, 0.0f, ballVelocity.Z);
        cameraForward.Y = 0.0f;
        float velocityAlignment =
            horizontalVelocity.LengthSquared() > 1.0e-8f &&
            cameraForward.LengthSquared() > 1.0e-8f
                ? Vector3.Dot(
                    Vector3.Normalize(cameraForward),
                    Vector3.Normalize(horizontalVelocity))
                : 0.0f;

        RecordEvent(
            "desktop-camera",
            $"sample={sample} ballPosition={Format(ballPosition)} " +
            $"ballVelocity={Format(ballVelocity)} cameraPosition={Format(cameraPosition)} " +
            $"cameraRotation={Format(cameraRotation)} cameraYaw={Format(cameraYaw)} " +
            $"offsetLength={Format(Vector3.Distance(ballPosition, cameraPosition))} " +
            $"rightY={Format(cameraRight.Y)} velocityAlignment={Format(velocityAlignment)}");
    }

    public static void RecordDesktopCameraPresentation(
        Vector3 ballRenderPosition,
        Vector3 cameraRenderPosition,
        Quaternion cameraRenderRotation,
        float expectedOffsetLength)
    {
        long sample = Interlocked.Increment(ref _cameraPresentationSamples);
        if (sample != 1 && sample % 90 != 0)
            return;

        Vector3 cameraRight = Vector3.Transform(Vector3.UnitX, cameraRenderRotation);
        float offsetLength = Vector3.Distance(ballRenderPosition, cameraRenderPosition);
        RecordEvent(
            "desktop-camera-presentation",
            $"sample={sample} ballRenderPosition={Format(ballRenderPosition)} " +
            $"cameraRenderPosition={Format(cameraRenderPosition)} " +
            $"cameraRenderRotation={Format(cameraRenderRotation)} " +
            $"offsetLength={Format(offsetLength)} " +
            $"offsetError={Format(MathF.Abs(offsetLength - expectedOffsetLength))} " +
            $"rightY={Format(cameraRight.Y)}");
    }

    private static string? ResolveOutputPath()
    {
        string? configured = Environment.GetEnvironmentVariable(DiagnosticsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try
        {
            string path = Path.GetFullPath(configured);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                path,
                $"timestamp={DateTimeOffset.Now:O} pid={Environment.ProcessId} event=session-start" +
                Environment.NewLine,
                Encoding.UTF8);
            return path;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"MonkeyBall runtime diagnostics could not open '{configured}': {exception}");
            return null;
        }
    }

    private static void WriteLine(string content)
    {
        string? path = OutputPath;
        if (path is null)
            return;

        string line =
            $"timestamp={DateTimeOffset.Now:O} pid={Environment.ProcessId} {content}" +
            Environment.NewLine;
        lock (Sync)
        {
            try
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"MonkeyBall runtime diagnostics could not write '{path}': {exception}");
            }
        }
    }

    private static string Format(Vector2 value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:R},{value.Y:R}");

    private static string Format(Vector3 value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:R},{value.Y:R},{value.Z:R}");

    private static string Format(Quaternion value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:R},{value.Y:R},{value.Z:R},{value.W:R}");

    private static string Format(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
        => value
            .Replace(' ', '_')
            .Replace(',', '+')
            .Replace('\r', '_')
            .Replace('\n', '_');
}
