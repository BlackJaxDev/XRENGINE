using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private static SceneNode AddMathBvhRig(
        SceneNode rootNode,
        MathBvhTestMode mode,
        MathIntersectionsWorldControllerComponent? controller)
    {
        var rigNode = rootNode.NewChild($"{mode} BVH Test");
        Transform rigTransform = rigNode.SetTransform<Transform>();

        ModelComponent? targetModel = null;
        List<Triangle>? triangles = null;
        if (mode is MathBvhTestMode.LegacyCpuMesh or MathBvhTestMode.GpuMesh)
        {
            targetModel = AddMathBvhMesh(rigNode, mode, out triangles);
            AddMathBvhMeshLight(rigNode);
        }

        MathBvhTestComponent test = rigNode.AddComponent<MathBvhTestComponent>()!;
        test.Configure(mode, targetModel, triangles);
        if (controller?.IsSpawningBenchmarkInstances != true)
        {
            var debugControls = rigNode.AddComponent<CustomUIComponent>()!;
            test.RegisterDebugControls(debugControls);
        }

        controller?.RegisterSubLabel(
            rigNode,
            rigTransform,
            mode switch
            {
                MathBvhTestMode.CpuScene => "green marker = CPU BVH/query parity",
                MathBvhTestMode.GpuScene => "green marker = GPU BVH/query parity",
                MathBvhTestMode.LegacyCpuMesh => "green marker = CPU mesh query parity",
                _ => "green marker = GPU mesh query parity",
            },
            6.8f);

        return rigNode;
    }

    private static ModelComponent AddMathBvhMesh(
        SceneNode rigNode,
        MathBvhTestMode mode,
        out List<Triangle> triangles)
    {
        XRMesh mesh = CreateMathBvhGridMesh(out triangles);
        XRMaterial material = XRMaterial.CreateLitColorMaterial(
            new ColorF4(0.34f, 0.38f, 0.42f, 1.0f),
            deferred: true);
        var subMesh = new SubMesh(mesh, material)
        {
            Name = mode == MathBvhTestMode.LegacyCpuMesh ? "LegacyCpuMeshBvhGrid" : "GpuMeshBvhGrid",
            UseGpuMeshBvh = mode == MathBvhTestMode.GpuMesh,
        };

        SceneNode modelNode = rigNode.NewChild("BVH Test Mesh");
        modelNode.SetTransform<Transform>();
        ModelComponent model = modelNode.AddComponent<ModelComponent>()!;
        model.Model = new Model([subMesh]);
        return model;
    }

    private static void AddMathBvhMeshLight(SceneNode rigNode)
    {
        SceneNode lightNode = rigNode.NewChild("BVH Test Mesh Light");
        Transform lightTransform = lightNode.SetTransform<Transform>();
        lightTransform.Translation = new Vector3(0.0f, 5.0f, 2.0f);

        PointLightComponent light = lightNode.AddComponent<PointLightComponent>()!;
        light.Name = "BVH Test Mesh Light";
        light.Color = new ColorF3(1.0f, 0.97f, 0.92f);
        light.DiffuseIntensity = 1.0f;
        light.Brightness = 24.0f;
        light.Radius = 14.0f;
        light.CastsShadows = false;
    }

    private static XRMesh CreateMathBvhGridMesh(out List<Triangle> triangles)
    {
        const int cellsPerAxis = 10;
        const float spacing = 0.8f;
        float halfExtent = cellsPerAxis * spacing * 0.5f;
        var meshTriangles = new List<VertexTriangle>(cellsPerAxis * cellsPerAxis * 2);
        var triangleList = new List<Triangle>(cellsPerAxis * cellsPerAxis * 2);

        for (int z = 0; z < cellsPerAxis; z++)
        for (int x = 0; x < cellsPerAxis; x++)
        {
            Vector3 p00 = GridPoint(x, z);
            Vector3 p10 = GridPoint(x + 1, z);
            Vector3 p01 = GridPoint(x, z + 1);
            Vector3 p11 = GridPoint(x + 1, z + 1);
            AddTriangle(p00, p11, p10);
            AddTriangle(p00, p01, p11);
        }

        triangles = triangleList;
        return new XRMesh(meshTriangles);

        Vector3 GridPoint(int x, int z)
        {
            float px = x * spacing - halfExtent;
            float pz = z * spacing - halfExtent;
            float py = 0.9f + MathF.Sin(px * 0.82f) * MathF.Cos(pz * 0.67f) * 0.48f;
            return new Vector3(px, py, pz);
        }

        void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            triangleList.Add(new Triangle(a, b, c));
            meshTriangles.Add(new VertexTriangle(
                new Vertex(a, GridNormal(a.X, a.Z)),
                new Vertex(b, GridNormal(b.X, b.Z)),
                new Vertex(c, GridNormal(c.X, c.Z))));
        }

        static Vector3 GridNormal(float x, float z)
        {
            float slopeX = 0.48f * 0.82f * MathF.Cos(x * 0.82f) * MathF.Cos(z * 0.67f);
            float slopeZ = -0.48f * 0.67f * MathF.Sin(x * 0.82f) * MathF.Sin(z * 0.67f);
            return Vector3.Normalize(new Vector3(-slopeX, 1.0f, -slopeZ));
        }
    }
}
