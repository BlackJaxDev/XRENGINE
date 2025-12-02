using System.Numerics;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;
using XREngine.Components.Lights;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    //All tests pertaining to shading the scene.

    //Code for lighting the scene.

    public static class Lighting
    {
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
                        probeComp.SetCaptureResolution(1024, false);
                        probeComp.RealtimeCapture = true;
                        probeComp.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;
                        probeComp.RealTimeCaptureUpdateInterval = TimeSpan.FromMilliseconds(Toggles.LightProbeCaptureMs);
                        if (Toggles.StopRealtimeCaptureSec is not null)
                            probeComp.StopRealtimeCaptureAfter = TimeSpan.FromSeconds(Toggles.StopRealtimeCaptureSec.Value);
                    }
                }
            }
        }

        public static void AddDirLight(SceneNode rootNode)
        {
            var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
            var dirLightTransform = dirLightNode.SetTransform<Transform>();
            dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
            //Face the light directly down
            dirLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-70.0f));
            //dirLightTransform.RegisterAnimationTick<Transform>(t => t.Rotation *= Quaternion.CreateFromAxisAngle(Globals.Backward, Engine.DilatedDelta));
            if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
                return;

            dirLightComp!.Name = "TestDirectionalLight";
            dirLightComp.Color = new Vector3(1, 1, 1);
            dirLightComp.DiffuseIntensity = 1.0f;
            dirLightComp.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
            dirLightComp.CastsShadows = true;
            dirLightComp.SetShadowMapResolution(4096, 4096);
        }

        public static void AddDirLight2(SceneNode rootNode)
        {
            var dirLightNode2 = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
            var dirLightTransform2 = dirLightNode2.SetTransform<Transform>();
            dirLightTransform2.Translation = new Vector3(0.0f, 10.0f, 0.0f);
            dirLightTransform2.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f);
            if (!dirLightNode2.TryAddComponent<DirectionalLightComponent>(out var dirLightComp2))
                return;

            dirLightComp2!.Name = "TestDirectionalLight2";
            dirLightComp2.Color = new Vector3(1.0f, 0.8f, 0.8f);
            dirLightComp2.DiffuseIntensity = 1.0f;
            dirLightComp2.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
            dirLightComp2.CastsShadows = false;
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