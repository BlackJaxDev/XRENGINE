using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Components.Physics;

internal readonly struct ConvexHullInput(Vector3[] positions, int[] indices)
{
    public Vector3[] Positions { get; } = positions;
    public int[] Indices { get; } = indices;
}

internal enum ConvexHullInputSource
{
    RuntimeMeshes,
    AssetMeshes,
}

internal readonly record struct ConvexHullInputBatch(ConvexHullInputSource Source, List<ConvexHullInput> Inputs, int SourceMeshCount)
{
    public string SourceLabel => Source switch
    {
        ConvexHullInputSource.RuntimeMeshes => "runtime render meshes",
        ConvexHullInputSource.AssetMeshes => "asset submeshes",
        _ => "collision meshes",
    };

    public int InputCount => Inputs.Count;
    public int VertexCount => Inputs.Sum(static input => input.Positions.Length);
    public int TriangleCount => Inputs.Sum(static input => input.Indices.Length / 3);
}

internal readonly record struct ConvexHullInputCollection(ConvexHullInputBatch Runtime, ConvexHullInputBatch Asset)
{
    public IEnumerable<ConvexHullInputBatch> EnumeratePreferredBatches()
    {
        if (Runtime.InputCount > 0)
            yield return Runtime;

        if (Asset.InputCount > 0)
            yield return Asset;
    }
}

internal static class ConvexHullUtility
{
    public static ConvexHullInputCollection CollectCollisionInputCollection(ModelComponent component)
    {
        RenderableMesh[] runtimeMeshes = [.. component.Meshes.ToArray()];
        List<ConvexHullInput> runtimeInputs = [.. EnumerateRuntimeMeshes(runtimeMeshes)];

        Model? model = component.Model;
        int assetMeshCount = model?.Meshes.Count ?? 0;
        List<ConvexHullInput> assetInputs = model is not null
            ? [.. EnumerateModelMeshes(model)]
            : [];

        return new ConvexHullInputCollection(
            new ConvexHullInputBatch(ConvexHullInputSource.RuntimeMeshes, runtimeInputs, runtimeMeshes.Length),
            new ConvexHullInputBatch(ConvexHullInputSource.AssetMeshes, assetInputs, assetMeshCount));
    }

    public static List<ConvexHullInput> CollectCollisionInputs(ModelComponent component)
    {
        ConvexHullInputCollection inputs = CollectCollisionInputCollection(component);
        foreach (ConvexHullInputBatch batch in inputs.EnumeratePreferredBatches())
            return batch.Inputs;

        return [];
    }

    public static IEnumerable<ConvexHullInput> EnumerateRuntimeMeshes(ModelComponent component)
        => EnumerateRuntimeMeshes([.. component.Meshes.ToArray()]);

    public static IEnumerable<ConvexHullInput> EnumerateRuntimeMeshes(IReadOnlyList<RenderableMesh> renderables)
    {
        for (int i = 0; i < renderables.Count; i++)
        {
            RenderableMesh renderable = renderables[i];
            if (TryGetRenderableMesh(renderable, out var mesh) && TryExtractMesh(mesh, out var input))
                yield return input;
        }
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

        foreach (RenderableMesh.RenderableLOD lod in renderable.GetLodSnapshot())
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

        Vector3[] sourcePositions = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            sourcePositions[i] = vertices[i].Position;

        int[] remap = new int[sourcePositions.Length];
        Array.Fill(remap, -1);

        List<Vector3> positions = new(sourcePositions.Length);
        List<int> sanitizedIndices = new(indices.Length);

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int index0 = indices[i];
            int index1 = indices[i + 1];
            int index2 = indices[i + 2];

            if ((uint)index0 >= (uint)sourcePositions.Length
                || (uint)index1 >= (uint)sourcePositions.Length
                || (uint)index2 >= (uint)sourcePositions.Length)
                continue;

            if (index0 == index1 || index1 == index2 || index0 == index2)
                continue;

            Vector3 p0 = sourcePositions[index0];
            Vector3 p1 = sourcePositions[index1];
            Vector3 p2 = sourcePositions[index2];
            if (!IsFinite(p0) || !IsFinite(p1) || !IsFinite(p2))
                continue;

            Vector3 edge01 = p1 - p0;
            Vector3 edge02 = p2 - p0;
            if (Vector3.Cross(edge01, edge02).LengthSquared() <= 1e-12f)
                continue;

            sanitizedIndices.Add(GetOrAddRemappedIndex(index0, sourcePositions, remap, positions));
            sanitizedIndices.Add(GetOrAddRemappedIndex(index1, sourcePositions, remap, positions));
            sanitizedIndices.Add(GetOrAddRemappedIndex(index2, sourcePositions, remap, positions));
        }

        if (positions.Count < 3 || sanitizedIndices.Count < 3)
            return false;

        input = new ConvexHullInput([.. positions], [.. sanitizedIndices]);
        return true;
    }

    private static int GetOrAddRemappedIndex(int sourceIndex, IReadOnlyList<Vector3> sourcePositions, int[] remap, List<Vector3> remappedPositions)
    {
        int existing = remap[sourceIndex];
        if (existing >= 0)
            return existing;

        int remappedIndex = remappedPositions.Count;
        remappedPositions.Add(sourcePositions[sourceIndex]);
        remap[sourceIndex] = remappedIndex;
        return remappedIndex;
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
