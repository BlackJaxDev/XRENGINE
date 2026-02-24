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
using XREngine.Rendering.Modeling;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private static readonly XRMeshModelingExportOptions MeshEditingPreviewExportOptions = new()
    {
        ValidateDocument = false
    };

    public static XRWorld CreateMeshEditingWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Mesh Editing Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        var pawn = Pawns.CreateMeshEditingPawn(rootNode, setUI);

        XRMesh sourceMesh = CreateEditableCubeMesh(2.5f);
        pawn.LoadFromXRMesh(sourceMesh);
        EditableMesh editableMesh = pawn.Mesh
            ?? throw new InvalidOperationException("MeshEditingPawnComponent failed to load source XRMesh.");

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

        pawn.TargetTransform = meshTransform;

        meshNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            RefreshEditableMesh(editableMesh, modelComp, wireDebug, material);
        });

        var world = new XRWorld("Mesh Editing World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static XRMesh CreateEditableCubeMesh(float size)
    {
        float h = size * 0.5f;
        List<Vertex> vertices =
        [
            new(new Vector3(-h, -h, -h)),
            new(new Vector3(h, -h, -h)),
            new(new Vector3(h, h, -h)),
            new(new Vector3(-h, h, -h)),
            new(new Vector3(-h, -h, h)),
            new(new Vector3(h, -h, h)),
            new(new Vector3(h, h, h)),
            new(new Vector3(-h, h, h))
        ];

        List<ushort> indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            3, 2, 6, 3, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            0, 3, 7, 0, 7, 4
        ];

        return new XRMesh(vertices, indices);
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
        ModelingMeshDocument document = EditableMeshConverter.FromEditable(
            editable,
            new ModelingMeshMetadata
            {
                SourcePrimitiveType = ModelingPrimitiveType.Triangles
            });

        XRMesh mesh = XRMeshModelingExporter.Export(document, MeshEditingPreviewExportOptions);
        return (mesh, mesh.Bounds);
    }
}
