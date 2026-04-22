using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Components.Scene;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Volumes;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Runtime.Bootstrap.Builders;

public static class BootstrapWorldFactory
{
    // Bootstrap worlds create and possess their own pawn during scene setup.
    // Prevent play-mode fallback from spawning a second default pawn over it.
    private static XRWorld CreateBootstrapWorld(string name, XRScene scene)
        => new(name, CreatePreplacedPawnGameMode(), scene);

    private static GameMode CreatePreplacedPawnGameMode()
        => new CustomGameMode
        {
            DefaultPlayerPawnClass = null,
        };

    public static XRWorld CreateSelectedWorld(bool setUI, bool isServer)
    {
        var settings = RuntimeBootstrapState.Settings;

        return settings.WorldKind switch
        {
            UnitTestWorldKind.Default => CreateUnitTestWorld(setUI, isServer),
            UnitTestWorldKind.NetworkingPose => CreateNetworkingPoseWorld(setUI, isServer),
            _ => BootstrapWorldBridge.Current?.CreateSpecializedWorld(settings.WorldKind, setUI, isServer) ?? CreateUnitTestWorld(setUI, isServer),
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

        DirectionalLightComponent? sunDirectionalLight = null;
        DirectionalLightComponent? moonDirectionalLight = null;

        if (settings.DirLight)
            sunDirectionalLight = BootstrapLightingBuilder.AddDirLight(rootNode);

        if (settings.SpotLight)
            AddSpotLight(rootNode);

        if (settings.DirLight2)
            moonDirectionalLight = AddDirLight2(rootNode);

        if (settings.PointLight)
            AddPointLight(rootNode);

        if (settings.SoundNode)
            BootstrapAudioWorldBuilder.AddSoundNode(rootNode);

        if (settings.IKTest)
            BootstrapAnimationWorldBuilder.AddIKTest(rootNode, targetNode => BootstrapEditorBridge.Current?.EnableTransformToolForNode(targetNode));

        if (settings.LightProbe != LightProbeMode.Off || settings.Skybox)
        {
            bool addLightProbe = settings.LightProbe != LightProbeMode.Off;
            bool addSkybox = settings.Skybox;
            string[] names = ["klippad_sunrise_2_4k", "warm_restaurant_4k", "overcast_soil_puresky_4k", "satara_night_4k", "studio_small_09_4k"];
            Random random = new();
            string skyTextureName = $"{names[random.Next(0, names.Length)]}.exr";
            Action? deferredStartupProbeCapture = null;

            if (addLightProbe)
                deferredStartupProbeCapture = BootstrapLightingBuilder.AddConfiguredLightProbes(
                    rootNode,
                    deferStartupCapture: addSkybox && settings.LightProbeCapture == LightProbeCaptureMode.Startup);

            if (addSkybox)
                AddSkyboxEnvironment(rootNode, skyTextureName, deferredStartupProbeCapture, settings.ProceduralSky, sunDirectionalLight, moonDirectionalLight);
        }

        if (settings.Mirror)
            AddMirror(rootNode);

        Debug.Out($"[VolumetricFog] BootstrapWorldFactory.CreateUnitTestWorld InitializeVolumetricFog = {settings.InitializeVolumetricFog}");
        if (settings.InitializeVolumetricFog)
            AddVolumetricFogVolume(rootNode, settings);

        if (settings.DynamicWaterQuad)
            BootstrapWaterBuilder.AddDynamicWaterPreview(rootNode);

        if (settings.AddPhysics)
            BootstrapPhysicsBuilder.AddPhysics(rootNode, settings.PhysicsBallCount);

        if (settings.Spline)
            BootstrapAnimationWorldBuilder.AddSpline(rootNode);

        if (settings.DeferredDecal)
            AddDeferredDecal(rootNode);

        BootstrapModelBuilder.ImportModels(desktopDir, rootNode, characterPawnModelParentNode ?? rootNode);

        return CreateBootstrapWorld("Default World", scene);
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

            Action setupEnvironment = () =>
            {
                BootstrapLightingBuilder.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
                BootstrapModelBuilder.AddSkybox(rootNode, null);
            };

            setupEnvironment();

            AddDefaultGridFloor(rootNode);

            return CreateBootstrapWorld("Default World", scene);
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

    private static void AddSkyboxEnvironment(
        SceneNode rootNode,
        string skyTextureName,
        Action? onSkyReady,
        bool useProceduralSky = false,
        DirectionalLightComponent? sunDirectionalLight = null,
        DirectionalLightComponent? moonDirectionalLight = null)
    {
        SkyboxComponent? skyboxComp = BootstrapModelBuilder.AddSkybox(rootNode, null);
        if (skyboxComp is null)
            return;

        if (useProceduralSky)
        {
            skyboxComp.Mode = ESkyboxMode.DynamicProcedural;
            skyboxComp.SunDirectionalLight = sunDirectionalLight;
            skyboxComp.SyncDirectionalLightWithSun = sunDirectionalLight is not null;
            skyboxComp.MoonDirectionalLight = moonDirectionalLight;
            skyboxComp.SyncDirectionalLightWithMoon = moonDirectionalLight is not null;
            onSkyReady?.Invoke();
            return;
        }

        string skyTexturePath = Engine.Assets.ResolveEngineAssetPath("Textures", skyTextureName);
        void StartSkyTextureLoad()
        {
            XRTexture2D.ScheduleLoadJob(
                skyTexturePath,
                onFinished: loadedTexture => Engine.EnqueueAppThreadTask(() =>
                {
                    skyboxComp.Projection = ESkyboxProjection.Equirectangular;
                    skyboxComp.Texture = loadedTexture;
                    skyboxComp.Mode = ESkyboxMode.Texture;
                    onSkyReady?.Invoke();
                }),
                onError: exception => Engine.EnqueueAppThreadTask(() =>
                {
                    Debug.LogWarning($"[BootstrapWorldFactory] Failed to load sky texture '{skyTexturePath}'. {exception.Message}");
                    onSkyReady?.Invoke();
                }),
                priority: JobPriority.Low);
        }

        void QueueSkyTextureLoad()
        {
            Engine.Jobs.Schedule(
                new LabeledActionJob(StartSkyTextureLoad, "BootstrapWorldFactory.StartSkyTextureLoad"),
                JobPriority.Low);
        }

        if (Engine.Windows.Count > 0 && !Engine.StartingUp)
        {
            QueueSkyTextureLoad();
            return;
        }

        void AfterWindowsCreated(GameStartupSettings _, GameState __)
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            QueueSkyTextureLoad();
        }

        Engine.AfterCreateWindows += AfterWindowsCreated;
    }

    private static void AddMirror(SceneNode rootNode)
    {
        SceneNode mirrorNode = rootNode.NewChild("MirrorNode");
        var mirrorTfm = mirrorNode.SetTransform<Transform>();
        mirrorTfm.Translation = new Vector3(0.0f, 0.0f, -20.0f);
        mirrorTfm.Scale = new Vector3(160.0f, 90.0f, 1.0f);
        _ = mirrorNode.AddComponent<MirrorCaptureComponent>();
    }

    private static void AddVolumetricFogVolume(SceneNode rootNode, UnitTestingWorldSettings settings)
    {
        var fogSettings = settings.VolumetricFog;

        SceneNode fogNode = rootNode.NewChild("VolumetricFogVolume");
        var fogTransform = fogNode.SetTransform<Transform>();
        fogTransform.Translation = new Vector3(fogSettings.Translation.X, fogSettings.Translation.Y, fogSettings.Translation.Z);

        var volume = fogNode.AddComponent<VolumetricFogVolumeComponent>()!;
        volume.HalfExtents = new Vector3(fogSettings.HalfExtents.X, fogSettings.HalfExtents.Y, fogSettings.HalfExtents.Z);
        volume.ScatteringColor = new ColorF3(fogSettings.ScatteringColor.R, fogSettings.ScatteringColor.G, fogSettings.ScatteringColor.B);
        volume.Density = fogSettings.Density;
        volume.NoiseScale = fogSettings.NoiseScale;
        volume.NoiseVelocity = new Vector3(fogSettings.NoiseVelocity.X, fogSettings.NoiseVelocity.Y, fogSettings.NoiseVelocity.Z);
        volume.NoiseThreshold = fogSettings.NoiseThreshold;
        volume.NoiseAmount = fogSettings.NoiseAmount;
        volume.EdgeFade = fogSettings.EdgeFade;
        volume.Anisotropy = fogSettings.Anisotropy;
        volume.LightContribution = fogSettings.LightContribution;
        volume.Priority = fogSettings.Priority;

        Debug.Out($"[VolumetricFog] BootstrapWorldFactory created volume node: Translation=({fogSettings.Translation.X}, {fogSettings.Translation.Y}, {fogSettings.Translation.Z}), HalfExtents=({fogSettings.HalfExtents.X}, {fogSettings.HalfExtents.Y}, {fogSettings.HalfExtents.Z}), Density={fogSettings.Density}");
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

    private static DirectionalLightComponent? AddDirLight2(SceneNode rootNode)
    {
        var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
        var dirLightTransform = dirLightNode.SetTransform<Transform>();
        dirLightTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
        dirLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f);
        if (!dirLightNode.TryAddComponent<DirectionalLightComponent>(out var dirLightComp))
            return null;

        dirLightComp!.Name = "TestDirectionalLight2";
        dirLightComp.Color = new Vector3(1.0f, 0.8f, 0.8f);
        dirLightComp.DiffuseIntensity = 1.0f;
        dirLightComp.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
        dirLightComp.CastsShadows = false;
        return dirLightComp;
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
