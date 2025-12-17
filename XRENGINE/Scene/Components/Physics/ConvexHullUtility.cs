using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Components.Physics;

internal readonly struct ConvexHullInput(Vector3[] positions, int[] indices)
{
    public Vector3[] Positions { get; } = positions;
    public int[] Indices { get; } = indices;
}

internal static class ConvexHullUtility
{
    public static List<ConvexHullInput> CollectCollisionInputs(ModelComponent component)
    {
        var inputs = EnumerateRuntimeMeshes(component).ToList();
        if (inputs.Count > 0)
            return inputs;

        return component.Model is Model model
            ? [.. EnumerateModelMeshes(model)]
            : [];
    }

    public static IEnumerable<ConvexHullInput> EnumerateRuntimeMeshes(ModelComponent component)
    {
        foreach (var renderable in component.Meshes.ToArray())
            if (TryGetRenderableMesh(renderable, out var mesh) && TryExtractMesh(mesh, out var input))
                yield return input;
    }

    public static IEnumerable<ConvexHullInput> EnumerateModelMeshes(Model model)
    {
        foreach (var subMesh in model.Meshes)
            if (TryGetAssetMesh(subMesh, out var mesh) && TryExtractMesh(mesh, out var input))
                yield return input;
    }

    private static bool TryGetRenderableMesh(RenderableMesh renderable, out XRMesh? mesh)
    {
        mesh = renderable.CurrentLODMesh;
        if (mesh is not null)
            return true;

        foreach (var lod in renderable.LODs)
        {
            if (lod.Renderer?.Mesh is XRMesh candidate)
            {
                mesh = candidate;
                return true;
            }
        }

        mesh = null;
        return false;
    }

    private static bool TryGetAssetMesh(SubMesh subMesh, out XRMesh? mesh)
    {
        foreach (var lod in subMesh.LODs)
        {
            if (lod.Mesh is XRMesh candidate)
            {
                mesh = candidate;
                return true;
            }
        }

        mesh = null;
        return false;
    }

    private static bool TryExtractMesh(XRMesh? mesh, out ConvexHullInput input)
    {
        input = default;
        if (mesh?.Vertices is not { Length: > 0 } vertices)
            return false;

        var indices = mesh.GetIndices(EPrimitiveType.Triangles);
        if (indices is null || indices.Length < 3)
            return false;

        Vector3[] positions = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            positions[i] = vertices[i].Position;

        input = new ConvexHullInput(positions, indices);
        return true;
    }
}
