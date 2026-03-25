using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Lights;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapLightingBuilder
{
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