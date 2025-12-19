using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static XRWorld CreatePhysxTestingWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        // Make it easy to see what's going on.
        Engine.Rendering.Settings.PhysicsVisualizeSettings.SetAllTrue();

        var scene = new XRScene("PhysX Testing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);
        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        AddPhysxPlayground(rootNode);

        var world = new XRWorld("PhysX Testing World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static void AddPhysxPlayground(SceneNode rootNode)
    {
        // Floor + lots of balls
        Physics.AddPhysicsFloor(rootNode);
        Physics.AddPhysicsSpheres(rootNode, count: 30, radius: 0.45f);

        // A simple box stack
        for (int i = 0; i < 10; i++)
        {
            AddDynamicBox(
                rootNode,
                name: $"Box_{i}",
                halfExtents: new Vector3(0.5f, 0.5f, 0.5f),
                position: new Vector3(-6.0f, 1.0f + i * 1.05f, 8.0f));
        }

        // Ramp
        AddStaticBox(
            rootNode,
            name: "Ramp",
            halfExtents: new Vector3(5.0f, 0.35f, 2.5f),
            position: new Vector3(6.0f, 0.65f, 10.0f),
            rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-20.0f)));

        // Some larger dynamic shapes to interact with
        AddDynamicBox(
            rootNode,
            name: "HeavyBox",
            halfExtents: new Vector3(1.25f, 1.25f, 1.25f),
            position: new Vector3(6.0f, 4.5f, 4.0f),
            density: 8.0f);

        // Visual reference axes
        var debugNode = rootNode.NewChild("ReferenceAxes");
        var debug = debugNode.AddComponent<DebugDrawComponent>()!;
        debug.AddLine(Vector3.Zero, Vector3.UnitX * 3, ColorF4.Red);
        debug.AddLine(Vector3.Zero, Vector3.UnitY * 3, ColorF4.Green);
        debug.AddLine(Vector3.Zero, Vector3.UnitZ * 3, ColorF4.Blue);
    }

    private static void AddDynamicBox(SceneNode rootNode, string name, Vector3 halfExtents, Vector3 position, float density = 1.0f)
    {
        var node = new SceneNode(rootNode) { Name = name };
        var tfm = node.SetTransform<RigidBodyTransform>();
        tfm.SetPositionAndRotation(position, Quaternion.Identity);

        var body = node.AddComponent<DynamicRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Box(halfExtents);
        body.Density = density;
        body.BodyFlags |= PhysicsRigidBodyFlags.EnableCcd | PhysicsRigidBodyFlags.EnableSpeculativeCcd;

        var model = node.AddComponent<ModelComponent>()!;
        var mat = XRMaterial.CreateLitColorMaterial(ColorF4.LightGray);
        mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        model.Model = new Model([
            new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, halfExtents * 2.0f), mat)
        ]);
    }

    private static void AddStaticBox(SceneNode rootNode, string name, Vector3 halfExtents, Vector3 position, Quaternion rotation)
    {
        var node = new SceneNode(rootNode) { Name = name };
        node.SetTransform<RigidBodyTransform>();

        var body = node.AddComponent<StaticRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Box(halfExtents);
        body.InitialPosition = position;
        body.InitialRotation = rotation;

        var model = node.AddComponent<ModelComponent>()!;
        var mat = XRMaterial.CreateLitColorMaterial(ColorF4.DarkGray);
        mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        model.Model = new Model([
            new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, halfExtents * 2.0f), mat)
        ]);
    }
}
