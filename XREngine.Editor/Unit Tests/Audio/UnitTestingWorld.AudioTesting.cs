using System.Numerics;
using XREngine.Audio.Steam;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static XRWorld CreateAudioTestingWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Audio Testing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        SceneNode? playerPawnNode = Pawns.CreatePlayerPawn(setUI, isServer, rootNode);

        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        AddAudioTestRoom(rootNode);
        AddAudioProbeVolume(rootNode);

        SceneNode sourceNode = AddLoopingTestSource(rootNode);
        if (playerPawnNode is not null)
            Audio.AttachMicTo(playerPawnNode, out _, out _, out _);

        if (Toggles.TransformTool)
        {
            UserInterface.EnableTransformToolForNode(sourceNode);
        }

        return CreateTrackedWorld("Audio Testing World", scene);
    }

    private static void AddAudioTestRoom(SceneNode rootNode)
    {
        AddAcousticPanel(rootNode, "Floor", new Vector3(0.0f, -0.25f, 0.0f), new Vector3(18.0f, 0.5f, 18.0f), ColorF4.DarkGray, SteamAudioMaterial.Concrete);
        AddAcousticPanel(rootNode, "Ceiling", new Vector3(0.0f, 6.5f, 0.0f), new Vector3(18.0f, 0.5f, 18.0f), ColorF4.Gray, SteamAudioMaterial.Concrete);
        AddAcousticPanel(rootNode, "BackWall", new Vector3(0.0f, 3.0f, -12.0f), new Vector3(18.0f, 6.0f, 0.5f), ColorF4.LightGray, SteamAudioMaterial.Concrete);
        AddAcousticPanel(rootNode, "FrontWall", new Vector3(0.0f, 3.0f, 12.0f), new Vector3(18.0f, 6.0f, 0.5f), ColorF4.LightGray, SteamAudioMaterial.Glass);
        AddAcousticPanel(rootNode, "LeftWall", new Vector3(-12.0f, 3.0f, 0.0f), new Vector3(0.5f, 6.0f, 18.0f), ColorF4.Gray, SteamAudioMaterial.Wood);
        AddAcousticPanel(rootNode, "RightWall", new Vector3(12.0f, 3.0f, 0.0f), new Vector3(0.5f, 6.0f, 18.0f), ColorF4.Gray, SteamAudioMaterial.Carpet);
    }

    private static SceneNode AddLoopingTestSource(SceneNode rootNode)
    {
        var soundNode = new SceneNode(rootNode) { Name = "AudioTestSource" };
        var transform = soundNode.SetTransform<Transform>();
        transform.Translation = new Vector3(0.0f, 1.5f, -4.0f);

        var soundComp = soundNode.AddComponent<AudioSourceComponent>()!;
        soundComp.Name = "Audio Test Tone";
        soundComp.RelativeToListener = false;
        soundComp.Gain = 0.35f;
        soundComp.ReferenceDistance = 1.0f;
        soundComp.MaxDistance = 40.0f;
        soundComp.RolloffFactor = 1.0f;
        soundComp.Loop = true;
        soundComp.PlayOnActivate = true;
        soundComp.StaticBuffer = Engine.Assets.LoadEngineAsset<AudioData>("Audio", "test16bit.wav");

        return soundNode;
    }

    private static void AddAudioProbeVolume(SceneNode rootNode)
    {
        var probeNode = new SceneNode(rootNode) { Name = "SteamAudioProbes" };
        var transform = probeNode.SetTransform<Transform>();
        transform.Translation = new Vector3(0.0f, 1.5f, 0.0f);

        var probes = probeNode.AddComponent<SteamAudioProbeComponent>()!;
        probes.VolumeExtents = new Vector3(10.0f, 3.0f, 10.0f);
        probes.ProbeSpacing = 2.0f;
        probes.ProbeHeight = 1.5f;
        probes.AutoGenerate = true;
        probes.AutoAttach = true;

        if (Toggles.TransformTool)
            UserInterface.EnableTransformToolForNode(probeNode);
    }

    private static void AddAcousticPanel(SceneNode rootNode, string name, Vector3 position, Vector3 size, ColorF4 color, SteamAudioMaterial material)
    {
        var node = new SceneNode(rootNode) { Name = name };
        var transform = node.SetTransform<Transform>();
        transform.Translation = position;

        var model = node.AddComponent<ModelComponent>()!;
        var renderMaterial = XRMaterial.CreateLitColorMaterial(color);
        renderMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        model.Model = new Model([
            new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, size), renderMaterial)
        ]);

        var geometry = node.AddComponent<SteamAudioGeometryComponent>()!;
        geometry.Material = material;
    }
}