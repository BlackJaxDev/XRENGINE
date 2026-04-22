using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Volumes;
using XREngine.Components.Scene;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Rendering;
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
        debug.RenderMesh3DBounds = Toggles.RenderMeshBounds;
        debug.RenderTransformDebugInfo = Toggles.RenderTransformDebugInfo;
        debug.RenderTransformLines = Toggles.RenderTransformLines;
        debug.RenderTransformCapsules = Toggles.RenderTransformCapsules;
        debug.RenderTransformPoints = Toggles.RenderTransformPoints;
        debug.RenderCullingVolumes = false;
        s.RecalcChildMatricesLoopType = Toggles.RecalcChildMatricesType;
        s.TickGroupedItemsInParallel = Toggles.TickGroupedItemsInParallel;
        s.RenderWindowsWhileInVR = Toggles.RenderWindowsWhileInVR;
        s.AllowShaderPipelines = Toggles.AllowShaderPipelines;
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
                    sunDirectionalLight: sunDirectionalLight,
                    moonDirectionalLight: moonDirectionalLight);
                if (skyboxComp is not null && !Toggles.ProceduralSky)
                    LoadSkyboxTextureAsync(skyboxComp, "Textures", $"{names[0]}.exr");
            }
        }

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
