using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Environment;
using XREngine.Components.Scene.Volumes;
using XREngine.Components.Scene;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private static bool _emulatedVrStereoPreviewHooked;

    // Unit-test worlds create and possess their own pawn during scene setup.
    // Prevent play-mode fallback from spawning a second default pawn over it.
    private static XRWorld CreateTrackedWorld(string name, XRScene scene)
    {
        var world = new XRWorld(name, CreatePreplacedPawnGameMode(), scene);
        world.Settings.PreviewOctrees = Toggles.VisualizeOctree;
        world.Settings.PreviewQuadtrees = Toggles.VisualizeQuadtree;
        Undo.TrackWorld(world);
        return world;
    }

    private static GameMode CreatePreplacedPawnGameMode()
        => new CustomGameMode
        {
            DefaultPlayerPawnClass = null,
        };

    private static void EnsureEmulatedVRStereoPreviewRenderingHooked()
    {
        if (_emulatedVrStereoPreviewHooked)
            return;

        if (!(Toggles.VRPawn && Toggles.EmulatedVRPawn && Toggles.PreviewVRStereoViews))
            return;

        _emulatedVrStereoPreviewHooked = true;

        Engine.Windows.PostAnythingAdded += OnWindowAddedForEmulatedVRStereoPreview;
        foreach (var window in Engine.Windows)
            OnWindowAddedForEmulatedVRStereoPreview(window);
    }

    private static void OnWindowAddedForEmulatedVRStereoPreview(XRWindow window)
        => Engine.InvokeOnMainThread(
            () => Engine.VRState.InitRenderEmulated(window),
            "UnitTestingWorld: Init emulated VR stereo",
            executeNowIfAlreadyMainThread: true);

    public static void ApplyRenderSettingsFromToggles()
    {
        var s = Engine.Rendering.Settings;
        var debug = Engine.EditorPreferences.Debug;
        var runtimeSettings = RuntimeBootstrapState.Settings;

        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderMeshBounds)))
            debug.RenderMesh3DBounds = Toggles.RenderMeshBounds;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformDebugInfo)))
            debug.RenderTransformDebugInfo = Toggles.RenderTransformDebugInfo;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformLines)))
            debug.RenderTransformLines = Toggles.RenderTransformLines;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformCapsules)))
            debug.RenderTransformCapsules = Toggles.RenderTransformCapsules;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformPoints)))
            debug.RenderTransformPoints = Toggles.RenderTransformPoints;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VisualizeOctree)))
            debug.Preview3DWorldOctree = Toggles.VisualizeOctree;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VisualizeQuadtree)))
            debug.Preview2DWorldQuadtree = Toggles.VisualizeQuadtree;
        debug.RenderCullingVolumes = false;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RecalcChildMatricesType)))
            s.RecalcChildMatricesLoopType = Toggles.RecalcChildMatricesType;
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.TickGroupedItemsInParallel)))
            s.TickGroupedItemsInParallel = Toggles.TickGroupedItemsInParallel;
        bool requiresDesktopVrWindow = Toggles.VRPawn && (Toggles.AllowEditingInVR || Toggles.PreviewVRStereoViews);
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderWindowsWhileInVR)) || requiresDesktopVrWindow)
            s.RenderWindowsWhileInVR = Toggles.RenderWindowsWhileInVR || requiresDesktopVrWindow;
        s.VrMirrorComposeFromEyeTextures = !requiresDesktopVrWindow;

        bool groupedRenderingSpecified = runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering));
        if (groupedRenderingSpecified)
        {
            UnitTestingOpenGLShaderLinkingSettings linkSettings = runtimeSettings.Rendering.OpenGL.ShaderLinking;
            s.AllowShaderPipelines = runtimeSettings.Rendering.OpenGL.AllowProgramPipelines;
            s.AllowBinaryProgramCaching = linkSettings.AllowBinaryProgramCaching;
            s.AsyncProgramBinaryUpload = linkSettings.AsyncProgramBinaryUpload;
            s.AsyncProgramCompilation = linkSettings.AsyncProgramCompilation;
            s.OpenGLProgramCompileLinkWorkerCount = linkSettings.ProgramCompileLinkWorkerCount;
            s.MaxAsyncShaderProgramsPerFrame = linkSettings.MaxAsyncShaderProgramsPerFrame;
            s.OpenGLShaderLinkStrategy = linkSettings.Strategy;
            s.OpenGLShaderCompilerThreadCount = linkSettings.DriverCompilerThreadCount;
            s.OpenGLParallelShaderCompileProbeEnabled = linkSettings.DriverParallelProbeEnabled;
            s.OpenGLParallelShaderCompileProbeTimeoutMs = linkSettings.DriverParallelProbeTimeoutMs;
            s.VulkanRenderTargetMode = runtimeSettings.Rendering.Vulkan.RenderTargetMode;
            s.Vulkan.Startup.FallbackPolicy = runtimeSettings.Rendering.BackendFallbackPolicy;
        }
        else if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowShaderPipelines)))
        {
            s.AllowShaderPipelines = Toggles.AllowShaderPipelines;
        }

        if (!groupedRenderingSpecified)
        {
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowBinaryProgramCaching)))
                s.AllowBinaryProgramCaching = Toggles.AllowBinaryProgramCaching;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AsyncProgramBinaryUpload)))
                s.AsyncProgramBinaryUpload = Toggles.AsyncProgramBinaryUpload;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AsyncProgramCompilation)))
                s.AsyncProgramCompilation = Toggles.AsyncProgramCompilation;
        }

        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowSkinning)))
            s.AllowSkinning = Toggles.AllowSkinning;
        Debug.Out($"[UnitTestingWorld] Applied render toggles AllowSkinning={s.AllowSkinning} AllowShaderPipelines={s.AllowShaderPipelines}");
        if (!groupedRenderingSpecified)
        {
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLProgramCompileLinkWorkerCount)))
                s.OpenGLProgramCompileLinkWorkerCount = Toggles.OpenGLProgramCompileLinkWorkerCount;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.MaxAsyncShaderProgramsPerFrame)))
                s.MaxAsyncShaderProgramsPerFrame = Toggles.MaxAsyncShaderProgramsPerFrame;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLShaderLinkStrategy)))
                s.OpenGLShaderLinkStrategy = Toggles.OpenGLShaderLinkStrategy;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLShaderCompilerThreadCount)))
                s.OpenGLShaderCompilerThreadCount = Toggles.OpenGLShaderCompilerThreadCount;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLParallelShaderCompileProbeEnabled)))
                s.OpenGLParallelShaderCompileProbeEnabled = Toggles.OpenGLParallelShaderCompileProbeEnabled;
            if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLParallelShaderCompileProbeTimeoutMs)))
                s.OpenGLParallelShaderCompileProbeTimeoutMs = Toggles.OpenGLParallelShaderCompileProbeTimeoutMs;
        }
        if (runtimeSettings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.SinglePassStereoVR)))
            s.RenderVRSinglePassStereo = Toggles.SinglePassStereoVR;
        if (Toggles.RenderPhysicsDebug)
            s.PhysicsVisualizeSettings.SetAllTrue();

        // Profiler frame logging is driven by EditorPreferences.Debug.EnableProfilerFrameLogging,
        // whose setter syncs Engine.Profiler.EnableFrameLogging automatically.
        // Do not override it here — that would discard the user's saved preference.

        EnsureEmulatedVRStereoPreviewRenderingHooked();
    }

    public static XRWorld CreateUberShaderWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Uber Shader Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);
        DirectionalLightComponent? sunDirectionalLight = null;
        DirectionalLightComponent? moonDirectionalLight = null;

        if (Toggles.DirLight)
            sunDirectionalLight = Lighting.AddDirLight(rootNode);

        if (Toggles.DirLight2)
            moonDirectionalLight = Lighting.AddDirLight2(rootNode);

        if (Toggles.SpotLight)
            Lighting.AddSpotLight(rootNode);

        if (Toggles.PointLight)
            Lighting.AddPointLight(rootNode);

        // IBL environment: without a light probe the Uber PBR shader renders near-black.
        // Use realtime progressive capture so the probe converges over several frames,
        // then auto-disables after 5 seconds once the scene is stable.
        if (Toggles.LightProbe != LightProbeMode.Off || Toggles.Skybox)
        {
            if (Toggles.LightProbe != LightProbeMode.Off)
                Lighting.AddConfiguredLightProbes(rootNode);

            if (Toggles.Skybox)
            {
                string[] names = ["warm_restaurant_4k"];
                var skyboxComp = Models.AddSkybox(
                    rootNode,
                    skyEquirect: null,
                    useProceduralSky: Toggles.ProceduralSky,
                    proceduralSkyAutoCycle: Toggles.ProceduralSkyAutoCycle,
                    proceduralSkyTimeOfDay: Toggles.ProceduralSkyTimeOfDay,
                    sunDirectionalLight: sunDirectionalLight,
                    moonDirectionalLight: moonDirectionalLight);
                if (skyboxComp is not null && !Toggles.ProceduralSky)
                    LoadSkyboxTextureAsync(skyboxComp, "Textures", $"{names[0]}.exr");
            }
        }

        if (Toggles.InitializeAtmosphericScattering)
            AddAtmosphericScattering(rootNode, sunDirectionalLight);

        AddUberShaderPreviewGrid(rootNode);
        AddUberShaderReferenceGrid(rootNode);

        return CreateTrackedWorld("Uber Shader World", scene);
    }

    private static void LoadSkyboxTextureAsync(SkyboxComponent skybox, params string[] relativePathFolders)
    {
        _ = LoadAndApplyAsync();

        async Task LoadAndApplyAsync()
        {
            try
            {
                var texture = await Engine.Assets.LoadEngineAssetAsync<XRTexture2D>(relativePathFolders).ConfigureAwait(false);
                Engine.EnqueueAppThreadTask(() =>
                {
                    if (skybox.IsDestroyed)
                        return;
                    skybox.Projection = ESkyboxProjection.Equirectangular;
                    skybox.Texture = texture;
                    skybox.Mode = ESkyboxMode.Texture;
                }, "UnitTestingWorld: Apply skybox texture");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "[UnitTestingWorld] Failed to load skybox texture.");
            }
        }
    }

    private static void AddAtmosphericScattering(SceneNode rootNode, DirectionalLightComponent? sunDirectionalLight)
    {
        var settings = Toggles.AtmosphericScattering;
        SceneNode atmosphereNode = rootNode.NewChild("AtmosphericScattering");
        var atmosphereTransform = atmosphereNode.SetTransform<Transform>();
        atmosphereTransform.Translation = new Vector3(settings.Translation.X, settings.Translation.Y, settings.Translation.Z);

        var atmosphere = atmosphereNode.AddComponent<AtmosphericScatteringComponent>()!;
        atmosphere.GroundRadius = settings.GroundRadius;
        atmosphere.AtmosphereHeight = settings.AtmosphereHeight;
        atmosphere.GroundLevelOffset = settings.GroundLevelOffset;
        atmosphere.SunIntensity = settings.SunIntensity;
        atmosphere.SunColor = new ColorF3(settings.SunColor.R, settings.SunColor.G, settings.SunColor.B);
        atmosphere.RayleighScaleHeight = settings.RayleighScaleHeight;
        atmosphere.MieScaleHeight = settings.MieScaleHeight;
        atmosphere.RayleighScattering = new Vector3(
            settings.RayleighScattering.R,
            settings.RayleighScattering.G,
            settings.RayleighScattering.B);
        atmosphere.MieScattering = new Vector3(
            settings.MieScattering.R,
            settings.MieScattering.G,
            settings.MieScattering.B);
        atmosphere.MieAnisotropy = settings.MieAnisotropy;
        atmosphere.ExposureScale = settings.ExposureScale;
        atmosphere.GroundAlbedo = settings.GroundAlbedo;
        atmosphere.SunDirectionalLight = sunDirectionalLight;
        atmosphere.SunSource = sunDirectionalLight is null
            ? AtmosphericScatteringComponent.ESunSource.DirectionOverride
            : AtmosphericScatteringComponent.ESunSource.ExplicitDirectionalLight;
    }

    //private static void AddPBRTestOrbs(SceneNode rootNode, float y)
    //{
    //    for (int metallic = 0; metallic < 10; metallic++)
    //        for (int roughness = 0; roughness < 10; roughness++)
    //            AddPBRTestOrb(rootNode, metallic / 10.0f, roughness / 10.0f, 0.5f, 0.5f, 10, 10, y);
    //}

    //private static void AddPBRTestOrb(SceneNode rootNode, float metallic, float roughness, float radius, float padding, int metallicCount, int roughnessCount, float y)
    //{
    //    var orb1 = new SceneNode(rootNode) { Name = "TestOrb1" };
    //    var orb1Transform = orb1.SetTransform<Transform>();

    //    //arrange in grid using metallic and roughness
    //    orb1Transform.Translation = new Vector3(
    //        (metallic * 2.0f - 1.0f) * (radius + padding) * metallicCount,
    //        y + padding + radius,
    //        (roughness * 2.0f - 1.0f) * (radius + padding) * roughnessCount);

    //    var orb1Model = orb1.AddComponent<ModelComponent>()!;
    //    var mat = XRMaterial.CreateLitColorMaterial(ColorF4.Red);
    //    mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
    //    mat.Parameter<ShaderFloat>("Roughness")!.Value = roughness;
    //    mat.Parameter<ShaderFloat>("Metallic")!.Value = metallic;
    //    orb1Model.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 32), mat)]);
    //}
}
