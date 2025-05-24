using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene;
using XREngine.Data.Components;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Components.Animation;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    /// <summary>
    /// Creates a test world with a variety of objects for testing purposes.
    /// </summary>
    /// <returns></returns>
    public static XRWorld CreateUnitTestWorld(bool setUI, bool isServer)
    {
        var s = Engine.Rendering.Settings;
        s.RenderMesh3DBounds = false;
        s.RenderTransformDebugInfo = true;
        s.RenderTransformLines = false;
        s.RenderTransformCapsules = false;
        s.RenderTransformPoints = false;
        s.RenderCullingVolumes = false;
        s.RecalcChildMatricesLoopType = Engine.Rendering.ELoopType.Sequential;
        s.TickGroupedItemsInParallel = true;
        s.RenderWindowsWhileInVR = true;
        s.AllowShaderPipelines = false; //Somehow, this lowers performance
        s.RenderVRSinglePassStereo = false;
        //s.PhysicsVisualizeSettings.SetAllTrue();

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

        SceneNode characterPawnModelParentNode = Pawns.CreatePlayerPawn(setUI, isServer, rootNode);

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
            string[] names = ["warm_restaurant_4k", "overcast_soil_puresky_4k", "studio_small_09_4k", "klippad_sunrise_2_4k", "satara_night_4k"];
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

        return new XRWorld("Default World", scene);
    }

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
}