using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    private static bool _emulatedVrStereoPreviewHooked;

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
            executeNowIfAlreadyMainThread: true);

    public static void ApplyRenderSettingsFromToggles()
    {
        var s = Engine.Rendering.Settings;
        s.RenderMesh3DBounds = Toggles.RenderMeshBounds;
        s.RenderTransformDebugInfo = Toggles.RenderTransformDebugInfo;
        s.RenderTransformLines = Toggles.RenderTransformLines;
        s.RenderTransformCapsules = Toggles.RenderTransformCapsules;
        s.RenderTransformPoints = Toggles.RenderTransformPoints;
        s.RenderCullingVolumes = false;
        s.RecalcChildMatricesLoopType = Toggles.RecalcChildMatricesType;
        s.TickGroupedItemsInParallel = Toggles.TickGroupedItemsInParallel;
        s.RenderWindowsWhileInVR = Toggles.RenderWindowsWhileInVR;
        s.AllowShaderPipelines = Toggles.AllowShaderPipelines;
        s.RenderVRSinglePassStereo = Toggles.SinglePassStereoVR;
        if (Toggles.RenderPhysicsDebug)
            s.PhysicsVisualizeSettings.SetAllTrue();

        Engine.Profiler.EnableFrameLogging = Toggles.EnableProfilerLogging;

        EnsureEmulatedVRStereoPreviewRenderingHooked();
    }

    /// <summary>
    /// Creates a test world with a variety of objects for testing purposes.
    /// </summary>
    /// <returns></returns>
    public static XRWorld CreateUnitTestWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        //UnityPackageExtractor.ExtractAsync(Path.Combine(desktopDir, "Animations.unitypackage"), Path.Combine(desktopDir, "Extracted"), true);

        //var anim = Engine.Assets.Load<Unity.UnityAnimationClip>(Path.Combine(desktopDir, "walk.anim"));
        //if (anim is not null)
        //{
        //    var anims = UnityConverter.ConvertFloatAnimation(anim);
        //}

        var scene = new XRScene("Main Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        if (Toggles.VisualizeOctree)
            rootNode.AddComponent<DebugVisualizeOctreeComponent>();

        SceneNode? characterPawnModelParentNode = Pawns.CreatePlayerPawn(setUI, isServer, rootNode);

        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        if (Toggles.SpotLight)
            Lighting.AddSpotLight(rootNode);

        if (Toggles.DirLight2)
            Lighting.AddDirLight2(rootNode);

        if (Toggles.PointLight)
            Lighting.AddPointLight(rootNode);

        if (Toggles.SoundNode)
            Audio.AddSoundNode(rootNode);

        if (Toggles.IKTest)
            AddIKTest(rootNode);

        if (Toggles.LightProbe || Toggles.Skybox)
        {
            string[] names = ["warm_restaurant_4k"/*, "overcast_soil_puresky_4k", "studio_small_09_4k", "klippad_sunrise_2_4k", "satara_night_4k"*/];
            Random r = new();
            XRTexture2D skyEquirect = Engine.Assets.LoadEngineAsset<XRTexture2D>("Textures", $"{names[r.Next(0, names.Length - 1)]}.exr");

            if (Toggles.LightProbe)
                Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
            if (Toggles.Skybox)
                Models.AddSkybox(rootNode, skyEquirect);
        }

        if (Toggles.Mirror)
            AddMirror(rootNode);

        if (Toggles.AddPhysics)
            Physics.AddPhysics(rootNode, Toggles.PhysicsBallCount);

        if (Toggles.Spline)
            AddSpline(rootNode);

        if (Toggles.DeferredDecal)
            AddDeferredDecal(rootNode);

        Models.ImportModels(desktopDir, rootNode, characterPawnModelParentNode ?? rootNode);

        var world = new XRWorld("Default World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    public static XRWorld CreateSelectedWorld(bool setUI, bool isServer)
        => Toggles.WorldKind switch
        {
            UnitTestWorldKind.MathIntersections => CreateMathIntersectionsWorld(setUI, isServer),
            UnitTestWorldKind.MeshEditing => CreateMeshEditingWorld(setUI, isServer),
            UnitTestWorldKind.UberShader => CreateUberShaderWorld(setUI, isServer),
            UnitTestWorldKind.PhysxTesting => CreatePhysxTestingWorld(setUI, isServer),
            _ => CreateUnitTestWorld(setUI, isServer),
        };

    private static void AddMirror(SceneNode rootNode)
    {
        SceneNode mirrorNode = rootNode.NewChild("MirrorNode");
        var mirrorTfm = mirrorNode.SetTransform<Transform>();
        mirrorTfm.Translation = new Vector3(0.0f, 0.0f, -20.0f);
        mirrorTfm.Scale = new Vector3(160.0f, 90.0f, 1.0f);
        var mirrorComp = mirrorNode.AddComponent<MirrorCaptureComponent>()!;
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

        //Let the user move the target
        UserInterface.EnableTransformToolForNode(targetNode);

        //string tree = ikTestRootNode.PrintTree();
        //Debug.Out(tree);
    }

    //Deferred decals are textures that are projected onto the scene geometry before the lighting pass using the GBuffer.
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
        Random r = new();
        float len = r.NextSingle() * 10.0f;
        int frameCount = r.Next(2, 10);
        for (int i = 0; i < frameCount; i++)
        {
            float t = i / (float)frameCount;
            Vector3 value = new(r.NextSingle() * 10.0f, r.NextSingle() * 10.0f, r.NextSingle() * 10.0f);
            Vector3 tangent = new(r.NextSingle() * 10.0f, r.NextSingle() * 10.0f, r.NextSingle() * 10.0f);
            anim.Keyframes.Add(new Vector3Keyframe(t, value, tangent, EVectorInterpType.Smooth));
        }
        anim.LengthInSeconds = len;
        spline!.Spline = anim;
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
