using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Components.Scene;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapWorldFactory
{
    public static XRWorld CreateSelectedWorld(bool setUI, bool isServer)
    {
        var settings = RuntimeBootstrapState.Settings;

        return settings.WorldKind switch
        {
            UnitTestWorldKind.Default => CreateUnitTestWorld(setUI, isServer),
            UnitTestWorldKind.NetworkingPose => CreateNetworkingPoseWorld(setUI, isServer),
            _ => BootstrapEditorBridge.Current?.CreateSpecializedWorld(settings.WorldKind, setUI, isServer) ?? CreateUnitTestWorld(setUI, isServer),
        };
    }

    public static XRWorld CreateUnitTestWorld(bool setUI, bool isServer)
    {
        var settings = RuntimeBootstrapState.Settings;
        BootstrapRenderSettings.Apply();

        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var scene = new XRScene("Main Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        if (settings.VisualizeOctree)
            rootNode.AddComponent<DebugVisualizeOctreeComponent>();

        SceneNode? characterPawnModelParentNode = BootstrapPawnFactory.CreatePlayerPawn(setUI, isServer, rootNode);

        if (settings.DirLight)
            BootstrapLightingBuilder.AddDirLight(rootNode);

        if (settings.SpotLight)
            AddSpotLight(rootNode);

        if (settings.DirLight2)
            AddDirLight2(rootNode);

        if (settings.PointLight)
            AddPointLight(rootNode);

        if (settings.SoundNode)
            BootstrapAudioBuilder.AddSoundNode(rootNode);

        if (settings.IKTest)
            AddIKTest(rootNode);

        if (settings.LightProbe || settings.Skybox)
        {
            string[] names = ["warm_restaurant_4k"];
            Random random = new();
            XRTexture2D skyEquirect = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", $"{names[random.Next(0, names.Length)]}.exr");

            if (settings.LightProbe)
                BootstrapLightingBuilder.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
            if (settings.Skybox)
                BootstrapModelBuilder.AddSkybox(rootNode, skyEquirect);
        }

        if (settings.Mirror)
            AddMirror(rootNode);

        if (settings.AddPhysics)
            BootstrapPhysicsBuilder.AddPhysics(rootNode, settings.PhysicsBallCount);

        if (settings.Spline)
            AddSpline(rootNode);

        if (settings.DeferredDecal)
            AddDeferredDecal(rootNode);

        BootstrapModelBuilder.ImportModels(desktopDir, rootNode, characterPawnModelParentNode ?? rootNode);

    return new XRWorld("Default World", scene);
    }

    public static XRWorld CreateDefaultEmptyWorld(bool setUI, bool isServer)
    {
        BootstrapRenderSettings.Apply();

        var settings = RuntimeBootstrapState.Settings;
        bool originalVrPawn = settings.VRPawn;
        bool originalLocomotion = settings.Locomotion;

        try
        {
            settings.VRPawn = false;
            settings.Locomotion = false;

            var scene = new XRScene("Main Scene");
            var rootNode = new SceneNode("Root Node");
            scene.RootNodes.Add(rootNode);

            rootNode.AddComponent<NetworkDiscoveryComponent>("Network Discovery");

            _ = BootstrapPawnFactory.CreatePlayerPawn(setUI, isServer, rootNode);
            BootstrapLightingBuilder.AddDirLight(rootNode);
            BootstrapLightingBuilder.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
            BootstrapModelBuilder.AddSkybox(rootNode, null);
            AddDefaultGridFloor(rootNode);

            return new XRWorld("Default World", scene);
        }
        finally
        {
            settings.VRPawn = originalVrPawn;
            settings.Locomotion = originalLocomotion;
        }
    }

    public static XRWorld CreateServerDefaultWorld()
        => RuntimeBootstrapState.Settings.WorldKind == UnitTestWorldKind.NetworkingPose
            ? CreateSelectedWorld(false, true)
            : CreateDefaultEmptyWorld(false, true);

    private static XRWorld CreateNetworkingPoseWorld(bool setUI, bool isServer)
    {
        BootstrapNetworkingWorldProfiles.ApplyNetworkingPoseProfile();
        return CreateUnitTestWorld(setUI, isServer);
    }

    private static void AddMirror(SceneNode rootNode)
    {
        SceneNode mirrorNode = rootNode.NewChild("MirrorNode");
        var mirrorTfm = mirrorNode.SetTransform<Transform>();
        mirrorTfm.Translation = new Vector3(0.0f, 0.0f, -20.0f);
        mirrorTfm.Scale = new Vector3(160.0f, 90.0f, 1.0f);
        _ = mirrorNode.AddComponent<MirrorCaptureComponent>();
    }

    private static void AddIKTest(SceneNode rootNode)
    {
        SceneNode ikTestRootNode = rootNode.NewChild();
        ikTestRootNode.Name = "IKTestRootNode";
        Transform tfmRoot = ikTestRootNode.GetTransformAs<Transform>(true)!;
        tfmRoot.Translation = new Vector3(0.0f, 0.0f, 0.0f);

        SceneNode ikTest1Node = ikTestRootNode.NewChild();
        ikTest1Node.Name = "IKTest1Node";
        Transform tfm1 = ikTest1Node.GetTransformAs<Transform>(true)!;
        tfm1.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        SceneNode ikTest2Node = ikTest1Node.NewChild();
        ikTest2Node.Name = "IKTest2Node";
        Transform tfm2 = ikTest2Node.GetTransformAs<Transform>(true)!;
        tfm2.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        var comp = ikTestRootNode.AddComponent<SingleTargetIKComponent>()!;
        comp.MaxIterations = 10;

        var targetNode = rootNode.NewChild();
        var targetTfm = targetNode.GetTransformAs<Transform>(true)!;
        targetTfm.Translation = new Vector3(2.0f, 5.0f, 0.0f);
        comp.TargetTransform = targetNode.Transform;

        BootstrapEditorBridge.Current?.EnableTransformToolForNode(targetNode);
    }

    private static void AddDeferredDecal(SceneNode rootNode)
    {
        var decalNode = new SceneNode(rootNode) { Name = "TestDecalNode" };
        var decalTfm = decalNode.SetTransform<Transform>();
        decalTfm.Translation = new Vector3(0.0f, 5.0f, 0.0f);
        decalTfm.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(70.0f));
        decalTfm.Scale = new Vector3(7.0f);
        var decalComp = decalNode.AddComponent<DeferredDecalComponent>()!;
        decalComp.Name = "TestDecal";
        decalComp.SetTexture(Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", "decal guide.png"));
    }

    private static void AddSpline(SceneNode rootNode)
    {
        var spline = rootNode.AddComponent<Spline3DPreviewComponent>();
        PropAnimVector3 anim = new();
        Random random = new();
        float len = random.NextSingle() * 10.0f;
        int frameCount = random.Next(2, 10);
        for (int i = 0; i < frameCount; i++)
        {
            float t = i / (float)frameCount;
            Vector3 value = new(random.NextSingle() * 10.0f, random.NextSingle() * 10.0f, random.NextSingle() * 10.0f);
            Vector3 tangent = new(random.NextSingle() * 10.0f, random.NextSingle() * 10.0f, random.NextSingle() * 10.0f);
            anim.Keyframes.Add(new Vector3Keyframe(t, value, tangent, EVectorInterpType.Smooth));
        }

        anim.LengthInSeconds = len;
        spline!.Spline = anim;
    }

    private static void AddDirLight2(SceneNode rootNode)
    {
        var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
        var dirLightTransform = dirLightNode.SetTransform<Transform>();
        dirLightTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
        dirLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f);
        if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
            return;

        dirLightComp!.Name = "TestDirectionalLight2";
        dirLightComp.Color = new Vector3(1.0f, 0.8f, 0.8f);
        dirLightComp.DiffuseIntensity = 1.0f;
        dirLightComp.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
        dirLightComp.CastsShadows = false;
    }

    private static void AddSpotLight(SceneNode rootNode)
    {
        var spotNode = new SceneNode(rootNode) { Name = "TestSpotLightNode" };
        var spotTransform = spotNode.SetTransform<Transform>();
        spotTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
        spotTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-90.0f));
        if (!spotNode.TryAddComponent<SpotLightComponent>(out var spotComp))
            return;

        spotComp!.Name = "TestSpotLight";
        spotComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
        spotComp.DiffuseIntensity = 10.0f;
        spotComp.Brightness = 5.0f;
        spotComp.Distance = 40.0f;
        spotComp.SetCutoffs(10, 40);
        spotComp.CastsShadows = true;
        spotComp.SetShadowMapResolution(2048, 2048);
    }

    private static void AddPointLight(SceneNode rootNode)
    {
        var pointNode = new SceneNode(rootNode) { Name = "TestPointLightNode" };
        var pointTransform = pointNode.SetTransform<Transform>();
        pointTransform.Translation = new Vector3(0.0f, 2.0f, 0.0f);
        if (!pointNode.TryAddComponent<PointLightComponent>(out var pointComp))
            return;

        pointComp!.Name = "TestPointLight";
        pointComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
        pointComp.DiffuseIntensity = 10.0f;
        pointComp.Brightness = 10.0f;
        pointComp.Radius = 10000.0f;
        pointComp.CastsShadows = true;
        pointComp.SetShadowMapResolution(1024, 1024);
    }

    private static void AddDefaultGridFloor(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("GridFloor");
        var debug = gridNode.AddComponent<DebugDrawComponent>()!;

        const float extent = 50.0f;
        const float step = 1.0f;
        const int majorEvery = 10;
        const float y = 0.0f;

        for (float x = -extent; x <= extent; x += step)
        {
            int xi = (int)MathF.Round(x);
            bool isAxis = xi == 0;
            bool isMajor = (xi % majorEvery) == 0;
            var color = isAxis ? XREngine.Data.Colors.ColorF4.White : isMajor ? XREngine.Data.Colors.ColorF4.Gray : XREngine.Data.Colors.ColorF4.DarkGray;
            debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
        }

        for (float z = -extent; z <= extent; z += step)
        {
            int zi = (int)MathF.Round(z);
            bool isAxis = zi == 0;
            bool isMajor = (zi % majorEvery) == 0;
            var color = isAxis ? XREngine.Data.Colors.ColorF4.White : isMajor ? XREngine.Data.Colors.ColorF4.Gray : XREngine.Data.Colors.ColorF4.DarkGray;
            debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
        }
    }
}