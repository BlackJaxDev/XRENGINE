using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SimpleScene.Util.ssBVH;
using XREngine;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Schedules GPU-backed skinned BVH rebuilds while guaranteeing that every GPU command
/// is dispatched from the render thread. Triangle expansion and BVH construction run on
/// a background thread so the render thread can keep pace with real-time workloads.
/// </summary>
internal sealed class SkinnedMeshBvhScheduler
{
    public static SkinnedMeshBvhScheduler Instance { get; } = new();

    private sealed record LeafRef(BVHNode<Triangle> Node, int[] TriangleIndices);

    private sealed class SkinnedBvhRefitCache
    {
        public BVH<Triangle>? Tree;
        public IReadOnlyList<IndexTriangle>? Triangles;
        public List<LeafRef> LeafRefs = [];
        public int VertexCount;
        public int TriangleCount;
    }

    private readonly ConditionalWeakTable<RenderableMesh, SkinnedBvhRefitCache> _cpuRefitCaches = new();

    private SkinnedMeshBvhScheduler()
    {
    }

    public Task<Result> Schedule(RenderableMesh mesh, int version)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Enqueue() => ExecuteOnRenderThread(mesh, version, tcs);

        if (Engine.IsRenderThread)
            Enqueue();
        else
            Engine.EnqueueMainThreadTask(Enqueue);

        return tcs.Task;
    }

    public Task<Result> Schedule(RenderableMesh mesh, int version, Vector3[] positions, AABB bounds, Matrix4x4 basis)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        var xrMesh = mesh.CurrentLODRenderer?.Mesh;
        var triangles = xrMesh?.Triangles;

        if (xrMesh is null || triangles is null || triangles.Count == 0)
        {
            var localized = mesh.EnsureLocalBounds(new SkinnedMeshBoundsCalculator.Result(positions, bounds, basis));
            tcs.TrySetResult(new Result(version, null, localized));
            return tcs.Task;
        }

        var boundsResult = mesh.EnsureLocalBounds(new SkinnedMeshBoundsCalculator.Result(positions, bounds, basis));
        var localizedPositions = boundsResult.Positions;

        if (localizedPositions is null || localizedPositions.Length == 0)
        {
            tcs.TrySetResult(new Result(version, null, boundsResult));
            return tcs.Task;
        }

        Engine.Jobs.Schedule(
            GenerateBvhJob(mesh, triangles, localizedPositions, version, boundsResult, tcs),
            error: ex => tcs.TrySetException(ex),
            canceled: () => tcs.TrySetCanceled()
        );

        return tcs.Task;
    }

    private static void ExecuteOnRenderThread(RenderableMesh mesh, int version, TaskCompletionSource<Result> tcs)
    {
        try
        {
            if (!SkinnedMeshBoundsCalculator.Instance.TryCompute(mesh, out var boundsResult))
            {
                tcs.TrySetResult(Result.Empty(version));
                return;
            }

            boundsResult = mesh.EnsureLocalBounds(boundsResult);

            var xrMesh = mesh.CurrentLODRenderer?.Mesh;
            var triangles = xrMesh?.Triangles;
            if (xrMesh is null || triangles is null || triangles.Count == 0)
            {
                tcs.TrySetResult(new Result(version, null, boundsResult));
                return;
            }

            var positions = boundsResult.Positions;
            if (positions is null || positions.Length == 0)
            {
                tcs.TrySetResult(new Result(version, null, boundsResult));
                return;
            }

            Engine.Jobs.Schedule(
                GenerateBvhJob(mesh, triangles, positions, version, boundsResult, tcs),
                error: ex => tcs.TrySetException(ex),
                canceled: () => tcs.TrySetCanceled()
            );
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static IEnumerable GenerateBvhJob(
        RenderableMesh mesh,
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        int version,
        SkinnedMeshBoundsCalculator.Result boundsResult,
        TaskCompletionSource<Result> tcs)
    {
        var task = Task.Run(() => BuildBvh(mesh, triangles, positions));
        yield return task;
        tcs.TrySetResult(new Result(version, task.Result, boundsResult));
    }

    private static BVH<Triangle>? BuildBvh(RenderableMesh mesh, IReadOnlyList<IndexTriangle> triangles, IReadOnlyList<Vector3> positions)
    {
        if (Engine.Rendering.Settings.UseSkinnedBvhRefitOptimize
            && TryRefitSkinnedBvh(mesh, triangles, positions, out var refitTree))
            return refitTree;

        var worldTriangles = new List<Triangle>(triangles.Count);
        int vertexCount = positions.Count;

        for (int i = 0; i < triangles.Count; i++)
        {
            var tri = triangles[i];
            if (tri.Point0 >= vertexCount || tri.Point1 >= vertexCount || tri.Point2 >= vertexCount)
                continue;

                worldTriangles.Add(new Triangle(
                positions[tri.Point0],
                positions[tri.Point1],
                positions[tri.Point2]));
        }

        return worldTriangles.Count > 0
                ? CacheSkinnedBvh(mesh, triangles, positions, new BVH<Triangle>(new TriangleAdapter(), worldTriangles))
            : null;
    }

    private static bool TryRefitSkinnedBvh(
        RenderableMesh mesh,
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        out BVH<Triangle>? tree)
    {
        tree = null;
        if (!Instance._cpuRefitCaches.TryGetValue(mesh, out var cache))
            return false;

        if (cache.Tree is null || cache.LeafRefs.Count == 0)
            return false;

        if (cache.TriangleCount != triangles.Count || cache.VertexCount != positions.Count)
            return false;

        if (!UpdateLeafTriangles(cache.LeafRefs, triangles, positions))
            return false;

        RefitInternalNodes(cache.Tree._rootBVH);

        if (Engine.Rendering.Settings.UseSkinnedBvhRefitOptimize)
            OptimizeTree(cache.Tree, cache.LeafRefs);

        tree = cache.Tree;
        return true;
    }

    private static BVH<Triangle> CacheSkinnedBvh(
        RenderableMesh mesh,
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        BVH<Triangle> tree)
    {
        if (!Engine.Rendering.Settings.UseSkinnedBvhRefitOptimize)
            return tree;

        var cache = Instance._cpuRefitCaches.GetValue(mesh, _ => new SkinnedBvhRefitCache());
        if (!TryBuildLeafRefs(tree, triangles, positions, out var leafRefs))
            return tree;

        cache.Tree = tree;
        cache.Triangles = triangles;
        cache.LeafRefs = leafRefs;
        cache.VertexCount = positions.Count;
        cache.TriangleCount = triangles.Count;
        return tree;
    }

    private static bool TryBuildLeafRefs(
        BVH<Triangle> tree,
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        out List<LeafRef> leafRefs)
    {
        leafRefs = [];
        int vertexCount = positions.Count;

        var triangleMap = new Dictionary<Triangle, Stack<int>>();
        for (int i = 0; i < triangles.Count; i++)
        {
            var tri = triangles[i];
            if (tri.Point0 >= vertexCount || tri.Point1 >= vertexCount || tri.Point2 >= vertexCount)
                continue;

            var triangle = new Triangle(
                positions[tri.Point0],
                positions[tri.Point1],
                positions[tri.Point2]);

            if (!triangleMap.TryGetValue(triangle, out var list))
            {
                list = new Stack<int>();
                triangleMap[triangle] = list;
            }

            list.Push(i);
        }

        var stack = new Stack<BVHNode<Triangle>>();
        stack.Push(tree._rootBVH);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.left is not null)
                stack.Push(node.left);
            if (node.right is not null)
                stack.Push(node.right);

            if (!node.IsLeaf || node.gobjects is null)
                continue;

            var indices = new int[node.gobjects.Count];
            for (int i = 0; i < node.gobjects.Count; i++)
            {
                var tri = node.gobjects[i];
                if (!triangleMap.TryGetValue(tri, out var list) || list.Count == 0)
                    return false;

                indices[i] = list.Pop();
            }

            leafRefs.Add(new LeafRef(node, indices));
        }

        return leafRefs.Count > 0;
    }

    private static bool UpdateLeafTriangles(
        List<LeafRef> leafRefs,
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions)
    {
        int vertexCount = positions.Count;
        foreach (var leafRef in leafRefs)
        {
            var node = leafRef.Node;
            var gobjects = node.gobjects;
            if (gobjects is null || gobjects.Count != leafRef.TriangleIndices.Length)
                return false;

            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int i = 0; i < leafRef.TriangleIndices.Length; i++)
            {
                int triIndex = leafRef.TriangleIndices[i];
                if ((uint)triIndex >= (uint)triangles.Count)
                    return false;

                var tri = triangles[triIndex];
                if (tri.Point0 >= vertexCount || tri.Point1 >= vertexCount || tri.Point2 >= vertexCount)
                    return false;

                var updated = new Triangle(
                    positions[tri.Point0],
                    positions[tri.Point1],
                    positions[tri.Point2]);

                gobjects[i] = updated;

                min = Vector3.Min(min, updated.A);
                min = Vector3.Min(min, updated.B);
                min = Vector3.Min(min, updated.C);

                max = Vector3.Max(max, updated.A);
                max = Vector3.Max(max, updated.B);
                max = Vector3.Max(max, updated.C);
            }

            node.box = new AABB(min, max);
        }

        return true;
    }

    private static void RefitInternalNodes(BVHNode<Triangle> root)
    {
        var stack = new Stack<BVHNode<Triangle>>();
        var postOrder = new List<BVHNode<Triangle>>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            postOrder.Add(node);
            if (node.left is not null)
                stack.Push(node.left);
            if (node.right is not null)
                stack.Push(node.right);
        }

        for (int i = postOrder.Count - 1; i >= 0; i--)
        {
            var node = postOrder[i];
            if (node.IsLeaf)
                continue;

            if (node.left is null || node.right is null)
                continue;

            AABB box = node.left.box;
            box.ExpandToInclude(node.right.box);
            node.box = box;
        }
    }

    private static void OptimizeTree(BVH<Triangle> tree, List<LeafRef> leafRefs)
    {
        if (tree.LEAF_OBJ_MAX != 1)
            return;

        tree._refitNodes.Clear();
        for (int i = 0; i < leafRefs.Count; i++)
        {
            var parent = leafRefs[i].Node.parent;
            if (parent is not null)
                tree._refitNodes.Add(parent);
        }

        if (tree._refitNodes.Count > 0)
            tree.Optimize();
    }

    internal readonly record struct Result(int Version, BVH<Triangle>? Tree, SkinnedMeshBoundsCalculator.Result Bounds)
    {
        public bool HasTree => Tree is not null;

        public static Result Empty(int version)
            => new(version, null, new SkinnedMeshBoundsCalculator.Result(Array.Empty<Vector3>(), default, Matrix4x4.Identity));
    }
}
