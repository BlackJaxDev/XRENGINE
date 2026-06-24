using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Runtime.Bootstrap;

namespace XREngine.Runtime.Bootstrap.Builders;

public static class BootstrapLightingBuilder
{
    public static Action? AddConfiguredLightProbes(SceneNode rootNode, bool deferStartupCapture = false)
    {
        var settings = RuntimeBootstrapState.Settings;
        bool shouldDeferStartupCapture = deferStartupCapture && settings.LightProbeCapture == LightProbeCaptureMode.Startup;
        LightProbeCaptureMode captureMode = shouldDeferStartupCapture
            ? LightProbeCaptureMode.None
            : settings.LightProbeCapture;

        switch (settings.LightProbe)
        {
            case LightProbeMode.Off:
                return null;
            case LightProbeMode.Single:
                Vector3 singlePosition = ToVector3(settings.LightProbeSinglePosition);
                IReadOnlyList<LightProbeComponent> probes = AddLightProbes(rootNode, 1, 1, 1, 1.0f, 1.0f, 1.0f, singlePosition, captureMode);
                return shouldDeferStartupCapture ? () => QueueProbeCaptures(probes) : null;
            case LightProbeMode.Grid:
            case LightProbeMode.ModelGrid:
                var counts = ClampProbeCounts(settings.LightProbeGridCounts);
                LightProbeGridSpawnerComponent spawner = AddInteractiveLightProbeGrid(
                    rootNode,
                    counts.X,
                    counts.Y,
                    counts.Z,
                    ToVector3(settings.LightProbeGridSpacing),
                    ToVector3(settings.LightProbeGridCenter),
                    settings.LightProbe == LightProbeMode.ModelGrid,
                    captureMode);
                return shouldDeferStartupCapture ? spawner.BeginSequentialCapture : null;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public static LightProbeGridSpawnerComponent AddInteractiveLightProbeGrid(SceneNode rootNode, int widthCount, int heightCount, int depthCount, Vector3 spacing, Vector3 center, bool usePlacementBoundsModels, LightProbeCaptureMode? captureModeOverride = null)
    {
        var settings = RuntimeBootstrapState.Settings;
        LightProbeCaptureMode captureMode = captureModeOverride ?? settings.LightProbeCapture;

        // Create the node detached so that OnBeginPlay does NOT fire during AddComponent.
        // This ensures all property setters run before SpawnGrid(), preventing the default
        // AutoCaptureOnActivate=true from leaking into spawned probes.
        var probeRoot = new SceneNode("LightProbeGridRoot");
        var spawner = probeRoot.AddComponent<LightProbeGridSpawnerComponent>()!;
        spawner.Name = "LightProbeGridSpawner";
        spawner.ProbeCounts = new IVector3(widthCount, heightCount, depthCount);
        spawner.Spacing = spacing;
        spawner.Offset = center;
        spawner.ConfigurePlacementBoundsModels(null, enabled: usePlacementBoundsModels);
        spawner.IrradianceResolution = 32;
        spawner.PreviewProbes = false;
        spawner.PreviewDisplay = LightProbeComponent.ERenderPreview.Environment;
        spawner.AdjustProbePositionsAgainstGeometry = true;
        spawner.ProbeCollisionRadius = 0.25f;
        spawner.PushOutPadding = 0.1f;
        spawner.MaxPushOutDistance = 8.0f;
        spawner.MaxPushOutSteps = 24;
        ApplyCaptureSettings(spawner, settings, captureMode);

        var customUi = probeRoot.AddComponent<CustomUIComponent>()!;
        customUi.Name = "Light Probe Grid Controls";
        customUi.AddButtonField(
            "Capture Probes",
            spawner.BeginSequentialCapture,
            "Queues the light probes one at a time so captures complete sequentially instead of all at once.");
        customUi.AddButtonField(
            "Cancel Capture",
            spawner.CancelSequentialCapture,
            "Stops the sequential capture process.");
        customUi.AddBoolField(
            "Show Probe Spheres",
            () => spawner.PreviewProbes,
            value => spawner.PreviewProbes = value,
            "Toggles the preview sphere for every spawned light probe.");
        customUi.AddButtonField(
            "Preview Environment",
            () => spawner.PreviewDisplay = LightProbeComponent.ERenderPreview.Environment,
            "Displays each probe's environment capture on the preview sphere.");
        customUi.AddButtonField(
            "Preview Irradiance",
            () => spawner.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance,
            "Displays each probe's irradiance texture on the preview sphere.");
        customUi.AddButtonField(
            "Preview Prefilter",
            () => spawner.PreviewDisplay = LightProbeComponent.ERenderPreview.Prefilter,
            "Displays each probe's prefilter texture on the preview sphere.");
        customUi.AddTextField(
            "Capture Status",
            () => spawner.CaptureStatus,
            "Reports sequential capture progress for the light probe grid.");

        // Parent into the tree LAST — this triggers OnBeginPlay → SpawnGrid with correct settings.
        probeRoot.Transform.Parent = rootNode.Transform;

        return spawner;
    }

    public static IReadOnlyList<LightProbeComponent> AddLightProbes(SceneNode rootNode, int heightCount, int widthCount, int depthCount, float height, float width, float depth, Vector3 center, LightProbeCaptureMode? captureModeOverride = null)
    {
        var settings = RuntimeBootstrapState.Settings;
        LightProbeCaptureMode captureMode = captureModeOverride ?? settings.LightProbeCapture;
        var probeRoot = new SceneNode(rootNode) { Name = "LightProbeRoot" };
        List<LightProbeComponent> probes = [];

        float halfWidth = width * 0.5f;
        float halfDepth = depth * 0.5f;

        for (int i = 0; i < heightCount; i++)
        {
            float h = i * height;
            for (int j = 0; j < widthCount; j++)
            {
                float w = j * width;
                for (int k = 0; k < depthCount; k++)
                {
                    float d = k * depth;

                    var probe = new SceneNode(probeRoot) { Name = $"LightProbe_{i}_{j}_{k}" };
                    var probeTransform = probe.SetTransform<Transform>();
                    probeTransform.Translation = center + new Vector3(w - halfWidth, h, d - halfDepth);
                    var probeComp = probe.AddComponent<LightProbeComponent>();

                    probeComp!.Name = "TestLightProbe";
                    probeComp.SetCaptureResolution(settings.LightProbeResolution, false);
                    probeComp.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
                    ApplyCaptureSettings(probeComp, settings, captureMode);
                    probes.Add(probeComp);
                }
            }
        }

        return probes;
    }

    private static void ApplyCaptureSettings(LightProbeComponent probe, UnitTestingWorldSettings settings)
        => ApplyCaptureSettings(probe, settings, settings.LightProbeCapture);

    private static void ApplyCaptureSettings(LightProbeComponent probe, UnitTestingWorldSettings settings, LightProbeCaptureMode captureMode)
    {
        probe.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(settings.LightProbeCaptureMs);
        probe.StopRealtimeCaptureAfter = captureMode == LightProbeCaptureMode.Realtime && settings.StopRealtimeCaptureSec is not null
            ? TimeSpan.FromSeconds(settings.StopRealtimeCaptureSec.Value)
            : null;

        switch (captureMode)
        {
            case LightProbeCaptureMode.None:
                probe.RealtimeCapture = false;
                probe.AutoCaptureOnActivate = false;
                break;
            case LightProbeCaptureMode.Startup:
                probe.RealtimeCapture = false;
                probe.AutoCaptureOnActivate = true;
                break;
            case LightProbeCaptureMode.Realtime:
                probe.AutoCaptureOnActivate = false;
                probe.RealtimeCapture = true;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void ApplyCaptureSettings(LightProbeGridSpawnerComponent spawner, UnitTestingWorldSettings settings)
        => ApplyCaptureSettings(spawner, settings, settings.LightProbeCapture);

    private static void ApplyCaptureSettings(LightProbeGridSpawnerComponent spawner, UnitTestingWorldSettings settings, LightProbeCaptureMode captureMode)
    {
        spawner.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(settings.LightProbeCaptureMs);
        spawner.StopRealtimeCaptureAfter = captureMode == LightProbeCaptureMode.Realtime && settings.StopRealtimeCaptureSec is not null
            ? TimeSpan.FromSeconds(settings.StopRealtimeCaptureSec.Value)
            : null;

        switch (captureMode)
        {
            case LightProbeCaptureMode.None:
                spawner.RealtimeCapture = false;
                spawner.AutoCaptureOnActivate = false;
                spawner.AutoSequentialCaptureOnBeginPlay = false;
                break;
            case LightProbeCaptureMode.Startup:
                spawner.RealtimeCapture = false;
                spawner.AutoCaptureOnActivate = false;
                spawner.AutoSequentialCaptureOnBeginPlay = true;
                break;
            case LightProbeCaptureMode.Realtime:
                spawner.AutoSequentialCaptureOnBeginPlay = false;
                spawner.AutoCaptureOnActivate = false;
                spawner.RealtimeCapture = true;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void QueueProbeCaptures(IReadOnlyList<LightProbeComponent> probes)
    {
        for (int i = 0; i < probes.Count; i++)
            probes[i].QueueCapture();
    }

    private static UnitTestingWorldSettings.ProbeGridCounts ClampProbeCounts(UnitTestingWorldSettings.ProbeGridCounts counts)
        => new()
        {
            X = Math.Max(1, counts.X),
            Y = Math.Max(1, counts.Y),
            Z = Math.Max(1, counts.Z),
        };

    private static Vector3 ToVector3(UnitTestingWorldSettings.TranslationXYZ value)
        => new(value.X, value.Y, value.Z);

    public static DirectionalLightComponent? AddDirLight(SceneNode rootNode)
    {
        var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
        var dirLightTransform = dirLightNode.SetTransform<Transform>();
        dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
        dirLightTransform.Rotation = Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(-120),
            float.DegreesToRadians(-55),
            0.0f);
        if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
            return null;

        dirLightComp!.Name = "TestDirectionalLight";
        dirLightComp.Color = new Vector3(1, 1, 1);
        dirLightComp.DiffuseIntensity = 1.0f;
        dirLightComp.Scale = new Vector3(100.0f, 100.0f, 900.0f);
        dirLightComp.CastsShadows = true;
        UnitTestingWorldSettings settings = RuntimeBootstrapState.Settings;
        ERenderLibrary renderBackend = settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering))
            ? settings.Rendering.RenderBackend
            : settings.RenderAPI;
        uint shadowResolution = renderBackend == ERenderLibrary.Vulkan ? 1024u : 4096u;
        dirLightComp.SetShadowMapResolution(shadowResolution, shadowResolution);
        return dirLightComp;
    }

    public static void AddDynamicDebugLights(
        SceneNode rootNode,
        int pointLightCount,
        int spotLightCount,
        int seed,
        bool castsShadows,
        bool forceShadowAtlas)
    {
        pointLightCount = Math.Max(0, pointLightCount);
        spotLightCount = Math.Max(0, spotLightCount);
        int totalLightCount = pointLightCount + spotLightCount;
        if (totalLightCount == 0)
            return;

        if (castsShadows && forceShadowAtlas)
            EnableRequiredDynamicLightShadowAtlases(pointLightCount, spotLightCount);

        Random random = new(seed);
        SceneNode rigNode = rootNode.NewChild("DynamicDebugLights");
        DynamicDebugLightState[] states = new DynamicDebugLightState[totalLightCount];
        int stateIndex = 0;

        for (int i = 0; i < pointLightCount; i++)
        {
            SceneNode pointNode = rigNode.NewChild($"DynamicPointLight_{i:D2}");
            Transform pointTransform = pointNode.SetTransform<Transform>();
            PointLightComponent pointLight = pointNode.AddComponent<PointLightComponent>()!;
            pointLight.Name = $"DynamicPointLight_{i:D2}";
            pointLight.Color = NextLightColor(random);
            pointLight.DiffuseIntensity = NextRange(random, 3.0f, 8.0f);
            pointLight.Brightness = NextRange(random, 2.0f, 6.0f);
            pointLight.Radius = NextRange(random, 10.0f, 28.0f);
            pointLight.CastsShadows = castsShadows;

            states[stateIndex++] = CreateDynamicLightState(random, pointTransform, lookAtTarget: false);
        }

        for (int i = 0; i < spotLightCount; i++)
        {
            SceneNode spotNode = rigNode.NewChild($"DynamicSpotLight_{i:D2}");
            Transform spotTransform = spotNode.SetTransform<Transform>();
            SpotLightComponent spotLight = spotNode.AddComponent<SpotLightComponent>()!;
            spotLight.Name = $"DynamicSpotLight_{i:D2}";
            spotLight.Color = NextLightColor(random);
            spotLight.DiffuseIntensity = NextRange(random, 5.0f, 12.0f);
            spotLight.Brightness = NextRange(random, 3.0f, 8.0f);
            spotLight.Distance = NextRange(random, 16.0f, 38.0f);
            float innerCutoff = NextRange(random, 8.0f, 18.0f);
            spotLight.SetCutoffs(innerCutoff, innerCutoff + NextRange(random, 18.0f, 34.0f));
            spotLight.CastsShadows = castsShadows;

            states[stateIndex++] = CreateDynamicLightState(random, spotTransform, lookAtTarget: true);
        }

        DynamicDebugLightRigComponent rig = rigNode.AddComponent<DynamicDebugLightRigComponent>()!;
        rig.Configure(states);
    }

    private static void EnableRequiredDynamicLightShadowAtlases(int pointLightCount, int spotLightCount)
    {
        var renderSettings = Engine.Rendering.Settings;

        if (pointLightCount > 0)
            renderSettings.UsePointShadowAtlas = true;

        if (spotLightCount > 0)
            renderSettings.UseSpotShadowAtlas = true;
    }

    private static DynamicDebugLightState CreateDynamicLightState(Random random, Transform transform, bool lookAtTarget)
    {
        Vector3 center = new(
            NextRange(random, -14.0f, 14.0f),
            lookAtTarget ? NextRange(random, 5.0f, 12.0f) : NextRange(random, 2.0f, 7.0f),
            NextRange(random, -14.0f, 14.0f));

        Vector3 positionAmplitude = new(
            NextRange(random, 2.0f, 7.0f),
            NextRange(random, 0.75f, 3.0f),
            NextRange(random, 2.0f, 7.0f));

        Vector3 positionFrequency = new(
            NextRange(random, 0.17f, 0.43f),
            NextRange(random, 0.11f, 0.31f),
            NextRange(random, 0.19f, 0.47f));

        Vector3 positionPhase = NextPhaseVector(random);
        Vector3 rotationFrequency = new(
            NextRange(random, 0.16f, 0.55f),
            NextRange(random, 0.14f, 0.50f),
            NextRange(random, 0.12f, 0.45f));

        Vector3 targetCenter = new(
            NextRange(random, -3.5f, 3.5f),
            NextRange(random, 0.5f, 3.0f),
            NextRange(random, -3.5f, 3.5f));

        Vector3 targetAmplitude = new(
            NextRange(random, 0.75f, 3.5f),
            NextRange(random, 0.25f, 1.5f),
            NextRange(random, 0.75f, 3.5f));

        Vector3 targetFrequency = new(
            NextRange(random, 0.09f, 0.24f),
            NextRange(random, 0.07f, 0.19f),
            NextRange(random, 0.10f, 0.27f));

        return new DynamicDebugLightState(
            transform,
            lookAtTarget,
            center,
            positionAmplitude,
            positionFrequency,
            positionPhase,
            rotationFrequency,
            NextPhaseVector(random),
            targetCenter,
            targetAmplitude,
            targetFrequency,
            NextPhaseVector(random));
    }

    private static Vector3 NextLightColor(Random random)
    {
        float hue = random.NextSingle();
        float saturation = NextRange(random, 0.65f, 1.0f);
        float value = NextRange(random, 0.80f, 1.0f);
        return HsvToRgb(hue, saturation, value);
    }

    private static Vector3 HsvToRgb(float hue, float saturation, float value)
    {
        float h = hue - MathF.Floor(hue);
        float scaled = h * 6.0f;
        int sector = (int)MathF.Floor(scaled);
        float fraction = scaled - sector;
        float p = value * (1.0f - saturation);
        float q = value * (1.0f - saturation * fraction);
        float t = value * (1.0f - saturation * (1.0f - fraction));

        return sector switch
        {
            0 => new Vector3(value, t, p),
            1 => new Vector3(q, value, p),
            2 => new Vector3(p, value, t),
            3 => new Vector3(p, q, value),
            4 => new Vector3(t, p, value),
            _ => new Vector3(value, p, q),
        };
    }

    private static float NextRange(Random random, float min, float max)
        => min + random.NextSingle() * (max - min);

    private static Vector3 NextPhaseVector(Random random)
        => new(
            NextRange(random, 0.0f, XRMath.TwoPIf),
            NextRange(random, 0.0f, XRMath.TwoPIf),
            NextRange(random, 0.0f, XRMath.TwoPIf));

    private readonly record struct DynamicDebugLightState(
        Transform Transform,
        bool LookAtTarget,
        Vector3 Center,
        Vector3 PositionAmplitude,
        Vector3 PositionFrequency,
        Vector3 PositionPhase,
        Vector3 RotationFrequency,
        Vector3 RotationPhase,
        Vector3 TargetCenter,
        Vector3 TargetAmplitude,
        Vector3 TargetFrequency,
        Vector3 TargetPhase);

    private sealed class DynamicDebugLightRigComponent : XRComponent
    {
        private DynamicDebugLightState[] _states = [];

        public void Configure(DynamicDebugLightState[] states)
        {
            SetField(ref _states, states);
            UpdateLights();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateLights);
            UpdateLights();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateLights);
        }

        private void UpdateLights()
        {
            float time = (float)Engine.ElapsedTime;

            for (int i = 0; i < _states.Length; i++)
                UpdateLight(_states[i], time);
        }

        private static void UpdateLight(in DynamicDebugLightState state, float time)
        {
            Vector3 position = Oscillate(
                time,
                state.Center,
                state.PositionAmplitude,
                state.PositionFrequency,
                state.PositionPhase);

            state.Transform.Translation = position;

            if (state.LookAtTarget)
            {
                Vector3 target = Oscillate(
                    time,
                    state.TargetCenter,
                    state.TargetAmplitude,
                    state.TargetFrequency,
                    state.TargetPhase);
                Vector3 forward = target - position;
                if (forward.LengthSquared() < 0.0001f)
                    forward = Globals.Forward;
                else
                    forward = Vector3.Normalize(forward);

                float roll = MathF.Sin(time * state.RotationFrequency.Z + state.RotationPhase.Z) * 0.35f;
                state.Transform.Rotation = CreateForwardRotation(forward, roll);
                return;
            }

            state.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(
                MathF.Sin(time * state.RotationFrequency.Y + state.RotationPhase.Y) * 1.2f,
                MathF.Sin(time * state.RotationFrequency.X + state.RotationPhase.X) * 0.8f,
                MathF.Sin(time * state.RotationFrequency.Z + state.RotationPhase.Z) * 1.0f);
        }

        private static Vector3 Oscillate(float time, Vector3 center, Vector3 amplitude, Vector3 frequency, Vector3 phase)
            => center + new Vector3(
                MathF.Sin(time * frequency.X + phase.X) * amplitude.X,
                MathF.Sin(time * frequency.Y + phase.Y) * amplitude.Y,
                MathF.Sin(time * frequency.Z + phase.Z) * amplitude.Z);

        private static Quaternion CreateForwardRotation(Vector3 forward, float rollRadians)
        {
            Vector3 backward = -forward;
            Vector3 upSeed = MathF.Abs(Vector3.Dot(backward, Globals.Up)) > 0.99f
                ? Globals.Right
                : Globals.Up;
            Vector3 right = Vector3.Normalize(Vector3.Cross(upSeed, backward));
            Vector3 up = Vector3.Normalize(Vector3.Cross(backward, right));

            if (rollRadians != 0.0f)
            {
                Quaternion roll = Quaternion.CreateFromAxisAngle(forward, rollRadians);
                right = Vector3.Transform(right, roll);
                up = Vector3.Transform(up, roll);
            }

            Matrix4x4 basis = new(
                right.X, right.Y, right.Z, 0.0f,
                up.X, up.Y, up.Z, 0.0f,
                backward.X, backward.Y, backward.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        }
    }
}
