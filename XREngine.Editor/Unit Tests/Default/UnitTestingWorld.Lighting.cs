using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Vectors;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Scene;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;
using XREngine.Components.Lights;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    //All tests pertaining to shading the scene.

    //Code for lighting the scene.

    public static class Lighting
    {
        public static void AddConfiguredLightProbes(SceneNode rootNode)
        {
            switch (Toggles.LightProbe)
            {
                case LightProbeMode.Off:
                    return;
                case LightProbeMode.Single:
                    Vector3 singlePosition = ToVector3(Toggles.LightProbeSinglePosition);
                    AddLightProbes(rootNode, 1, 1, 1, 1.0f, 1.0f, 1.0f, singlePosition);
                    return;
                case LightProbeMode.Grid:
                case LightProbeMode.ModelGrid:
                    var counts = ClampProbeCounts(Toggles.LightProbeGridCounts);
                    AddInteractiveLightProbeGrid(
                        rootNode,
                        counts.X,
                        counts.Y,
                        counts.Z,
                        ToVector3(Toggles.LightProbeGridSpacing),
                        ToVector3(Toggles.LightProbeGridCenter),
                        Toggles.LightProbe == LightProbeMode.ModelGrid);
                    return;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static void AddLightProbes(SceneNode rootNode, int heightCount, int widthCount, int depthCount, float height, float width, float depth, Vector3 center)
        {
            var probeRoot = new SceneNode(rootNode) { Name = "LightProbeRoot" };

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
                        probeComp.SetCaptureResolution(Toggles.LightProbeResolution, false);
                        probeComp.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
                        ApplyCaptureSettings(probeComp);
                    }
                }
            }
        }

        public static void AddInteractiveLightProbeGrid(SceneNode rootNode, int widthCount, int heightCount, int depthCount, Vector3 spacing, Vector3 center, bool usePlacementBoundsModels)
        {
            var probeRoot = new SceneNode(rootNode) { Name = "LightProbeGridRoot" };
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
            ApplyCaptureSettings(spawner);

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
        }

        private static void ApplyCaptureSettings(LightProbeComponent probe)
        {
            probe.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(Toggles.LightProbeCaptureMs);
            probe.StopRealtimeCaptureAfter = Toggles.LightProbeCapture == LightProbeCaptureMode.Realtime && Toggles.StopRealtimeCaptureSec is not null
                ? TimeSpan.FromSeconds(Toggles.StopRealtimeCaptureSec.Value)
                : null;

            switch (Toggles.LightProbeCapture)
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

        private static void ApplyCaptureSettings(LightProbeGridSpawnerComponent spawner)
        {
            spawner.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(Toggles.LightProbeCaptureMs);
            spawner.StopRealtimeCaptureAfter = Toggles.LightProbeCapture == LightProbeCaptureMode.Realtime && Toggles.StopRealtimeCaptureSec is not null
                ? TimeSpan.FromSeconds(Toggles.StopRealtimeCaptureSec.Value)
                : null;

            switch (Toggles.LightProbeCapture)
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

        private static Settings.ProbeGridCounts ClampProbeCounts(Settings.ProbeGridCounts counts)
            => new()
            {
                X = Math.Max(1, counts.X),
                Y = Math.Max(1, counts.Y),
                Z = Math.Max(1, counts.Z),
            };

        private static Vector3 ToVector3(Settings.TranslationXYZ value)
            => new(value.X, value.Y, value.Z);

        public static DirectionalLightComponent? AddDirLight(SceneNode rootNode)
        {
            var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
            var dirLightTransform = dirLightNode.SetTransform<Transform>();
            dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
            dirLightTransform.Rotation = Quaternion.CreateFromYawPitchRoll(
                XRMath.DegToRad(-120.0f),
                XRMath.DegToRad(-55.0f),
                0.0f);
            //dirLightTransform.RegisterAnimationTick<Transform>(t => t.Rotation *= Quaternion.CreateFromAxisAngle(Globals.Backward, Engine.DilatedDelta));
            if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
                return null;

            dirLightComp!.Name = "TestDirectionalLight";
            dirLightComp.Color = new Vector3(1, 1, 1);
            dirLightComp.DiffuseIntensity = 1.0f;
            dirLightComp.Scale = new Vector3(100.0f, 100.0f, 900.0f);
            dirLightComp.CastsShadows = true;
            dirLightComp.SetShadowMapResolution(4096, 4096);
            return dirLightComp;
        }

        public static DirectionalLightComponent? AddDirLight2(SceneNode rootNode)
        {
            var dirLightNode2 = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
            var dirLightTransform2 = dirLightNode2.SetTransform<Transform>();
            dirLightTransform2.Translation = new Vector3(0.0f, 10.0f, 0.0f);
            dirLightTransform2.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f);
            if (!dirLightNode2.TryAddComponent<DirectionalLightComponent>(out var dirLightComp2))
                return null;

            dirLightComp2!.Name = "TestDirectionalLight2";
            dirLightComp2.Color = new Vector3(1.0f, 0.8f, 0.8f);
            dirLightComp2.DiffuseIntensity = 1.0f;
            dirLightComp2.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
            dirLightComp2.CastsShadows = false;
            return dirLightComp2;
        }

        public static void AddSpotLight(SceneNode rootNode)
        {
            var spotLightNode = new SceneNode(rootNode) { Name = "TestSpotLightNode" };
            var spotLightTransform = spotLightNode.SetTransform<Transform>();
            spotLightTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
            spotLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-90.0f));
            if (!spotLightNode.TryAddComponent<SpotLightComponent>(out var spotLightComp))
                return;

            spotLightComp!.Name = "TestSpotLight";
            spotLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
            spotLightComp.DiffuseIntensity = 10.0f;
            spotLightComp.Brightness = 5.0f;
            spotLightComp.Distance = 40.0f;
            spotLightComp.SetCutoffs(10, 40);
            spotLightComp.CastsShadows = true;
            spotLightComp.SetShadowMapResolution(2048, 2048);
        }

        public static void AddPointLight(SceneNode rootNode)
        {
            var pointLight = new SceneNode(rootNode) { Name = "TestPointLightNode" };
            var pointLightTransform = pointLight.SetTransform<Transform>();
            pointLightTransform.Translation = new Vector3(0.0f, 2.0f, 0.0f);
            if (!pointLight.TryAddComponent<PointLightComponent>(out var pointLightComp))
                return;

            pointLightComp!.Name = "TestPointLight";
            pointLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
            pointLightComp.DiffuseIntensity = 10.0f;
            pointLightComp.Brightness = 10.0f;
            pointLightComp.Radius = 10000.0f;
            pointLightComp.CastsShadows = true;
            pointLightComp.SetShadowMapResolution(1024, 1024);
        }
    }
}
