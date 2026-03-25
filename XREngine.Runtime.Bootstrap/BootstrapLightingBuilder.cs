using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Lights;
using XREngine.Data.Vectors;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapLightingBuilder
{
    public static LightProbeGridSpawnerComponent AddInteractiveLightProbeGrid(SceneNode rootNode, int widthCount, int heightCount, int depthCount, Vector3 spacing, Vector3 center)
    {
        // Create the node detached so that OnBeginPlay does NOT fire during AddComponent.
        // This ensures all property setters run before SpawnGrid(), preventing the default
        // AutoCaptureOnActivate=true from leaking into spawned probes.
        var probeRoot = new SceneNode("LightProbeGridRoot");
        var spawner = probeRoot.AddComponent<LightProbeGridSpawnerComponent>()!;
        spawner.Name = "LightProbeGridSpawner";
        spawner.ProbeCounts = new IVector3(widthCount, heightCount, depthCount);
        spawner.Spacing = spacing;
        spawner.Offset = center;
        spawner.RealtimeCapture = false;
        spawner.AutoCaptureOnActivate = false;
        spawner.AutoSequentialCaptureOnBeginPlay = false;
        spawner.IrradianceResolution = 32;
        spawner.PreviewProbes = false;
        spawner.PreviewDisplay = LightProbeComponent.ERenderPreview.Environment;
        spawner.AdjustProbePositionsAgainstGeometry = true;
        spawner.ProbeCollisionRadius = 0.25f;
        spawner.PushOutPadding = 0.1f;
        spawner.MaxPushOutDistance = 8.0f;
        spawner.MaxPushOutSteps = 24;

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

    public static void AddLightProbes(SceneNode rootNode, int heightCount, int widthCount, int depthCount, float height, float width, float depth, Vector3 center)
    {
        var settings = RuntimeBootstrapState.Settings;
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
                    probeComp.SetCaptureResolution(128, false);
                    probeComp.RealtimeCapture = true;
                    probeComp.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
                    probeComp.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(settings.LightProbeCaptureMs);
                    if (settings.StopRealtimeCaptureSec is not null)
                        probeComp.StopRealtimeCaptureAfter = TimeSpan.FromSeconds(settings.StopRealtimeCaptureSec.Value);
                }
            }
        }
    }

    public static void AddDirLight(SceneNode rootNode)
    {
        var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
        var dirLightTransform = dirLightNode.SetTransform<Transform>();
        dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
        dirLightTransform.Rotation = Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(-120),
            float.DegreesToRadians(-55),
            0.0f);
        if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
            return;

        dirLightComp!.Name = "TestDirectionalLight";
        dirLightComp.Color = new Vector3(1, 1, 1);
        dirLightComp.DiffuseIntensity = 1.0f;
        dirLightComp.Scale = new Vector3(100.0f, 100.0f, 900.0f);
        dirLightComp.CastsShadows = true;
        dirLightComp.SetShadowMapResolution(4096, 4096);
    }
}