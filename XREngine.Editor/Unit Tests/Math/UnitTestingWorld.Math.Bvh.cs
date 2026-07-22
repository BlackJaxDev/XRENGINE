using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
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
            targetModel = AddMathBvhMesh(rigNode, mode, out triangles);

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
        XRMaterial material = XRMaterial.CreateUnlitColorMaterialForward(
            new ColorF4(0.12f, 0.31f, 0.46f, 1.0f));
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

    private static XRMesh CreateMathBvhGridMesh(out List<Triangle> triangles)
    {
        const int cellsPerAxis = 10;
        const float spacing = 0.8f;
        float halfExtent = cellsPerAxis * spacing * 0.5f;
        var positions = new List<Vector3>(cellsPerAxis * cellsPerAxis * 6);
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
        return XRMesh.CreateTriangles([.. positions]);

        Vector3 GridPoint(int x, int z)
        {
            float px = x * spacing - halfExtent;
            float pz = z * spacing - halfExtent;
            float py = 0.9f + MathF.Sin(px * 0.82f) * MathF.Cos(pz * 0.67f) * 0.48f;
            return new Vector3(px, py, pz);
        }

        void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            positions.Add(a);
            positions.Add(b);
            positions.Add(c);
            triangleList.Add(new Triangle(a, b, c));
        }
    }
}
