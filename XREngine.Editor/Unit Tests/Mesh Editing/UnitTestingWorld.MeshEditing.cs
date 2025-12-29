using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Modeling;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static XRWorld CreateMeshEditingWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Mesh Editing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        var pawn = Pawns.CreateMeshEditingPawn(rootNode, setUI);

        var editableMesh = CreateEditableCube(2.5f);
        var meshNode = rootNode.NewChild("EditableMeshNode");
        var meshTransform = meshNode.SetTransform<Transform>();
        meshTransform.Translation = new Vector3(0.0f, 1.5f, 6.0f);
        meshTransform.Rotation = Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(-15.0f));

        var modelComp = meshNode.AddComponent<ModelComponent>()!;
        var wireNode = meshNode.NewChild("EditableMeshWire");
        wireNode.SetTransform<Transform>();
        var wireDebug = wireNode.AddComponent<DebugDrawComponent>()!;

        XRMaterial material = XRMaterial.CreateLitColorMaterial(ColorF4.LightTeal);
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;

        pawn.Mesh = editableMesh;
        pawn.TargetTransform = meshTransform;

        meshNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            RefreshEditableMesh(editableMesh, modelComp, wireDebug, material);
        });

        var world = new XRWorld("Mesh Editing World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static EditableMesh CreateEditableCube(float size)
    {
        float h = size * 0.5f;
        Vector3[] vertices =
        [
            new(-h, -h, -h),
            new(h, -h, -h),
            new(h, h, -h),
            new(-h, h, -h),
            new(-h, -h, h),
            new(h, -h, h),
            new(h, h, h),
            new(-h, h, h)
        ];

        int[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            3, 2, 6, 3, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            0, 3, 7, 0, 7, 4
        ];

        return new EditableMesh(vertices, indices);
    }

    private static void RefreshEditableMesh(EditableMesh editable, ModelComponent modelComp, DebugDrawComponent wireDebug, XRMaterial material)
    {
        wireDebug.ClearShapes();
        foreach (var edge in editable.Edges)
        {
            Vector3 a = editable.Vertices[edge.A];
            Vector3 b = editable.Vertices[edge.B];
            wireDebug.AddLine(a, b, ColorF4.Yellow);
        }

        (XRMesh mesh, AABB bounds) = BuildRenderableMesh(editable);
        var subMesh = new SubMesh(mesh, material)
        {
            Bounds = bounds,
            CullingBounds = bounds,
        };
        modelComp.Model = new Model(subMesh);
        modelComp.RenderBounds = true;
    }

    private static (XRMesh Mesh, AABB Bounds) BuildRenderableMesh(EditableMesh editable)
    {
        var triangles = new List<VertexTriangle>(editable.Faces.Count);
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);

        foreach (var face in editable.Faces)
        {
            Vector3 a = editable.Vertices[face.A];
            Vector3 b = editable.Vertices[face.B];
            Vector3 c = editable.Vertices[face.C];
            Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));

            triangles.Add(new VertexTriangle(
                new Vertex(a, normal, Vector2.Zero),
                new Vertex(b, normal, Vector2.Zero),
                new Vertex(c, normal, Vector2.Zero)));

            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }

        XRMesh mesh = XRMesh.Create(triangles);
        return (mesh, new AABB(min, max));
    }
}
