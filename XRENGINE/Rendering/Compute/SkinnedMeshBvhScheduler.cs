using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
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

    public Task<Result> Schedule(RenderableMesh mesh, int version, Vector3[] positions, AABB bounds)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);

        var xrMesh = mesh.CurrentLODRenderer?.Mesh;
        var triangles = xrMesh?.Triangles;

        if (xrMesh is null || triangles is null || triangles.Count == 0)
        {
            tcs.TrySetResult(Result.Empty(version));
            return tcs.Task;
        }

        var boundsResult = new SkinnedMeshBoundsCalculator.Result(positions, bounds);

        Engine.Jobs.Schedule(
            GenerateBvhJob(triangles, positions, version, boundsResult, tcs),
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

            var xrMesh = mesh.CurrentLODRenderer?.Mesh;
            var triangles = xrMesh?.Triangles;
            if (xrMesh is null || triangles is null || triangles.Count == 0)
            {
                tcs.TrySetResult(new Result(version, null, boundsResult));
                return;
            }

            var positions = boundsResult.Positions;
            Engine.Jobs.Schedule(
                GenerateBvhJob(triangles, positions, version, boundsResult, tcs),
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
        IReadOnlyList<IndexTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        int version,
        SkinnedMeshBoundsCalculator.Result boundsResult,
        TaskCompletionSource<Result> tcs)
    {
        var task = Task.Run(() => BuildBvh(triangles, positions));
        yield return task;
        tcs.TrySetResult(new Result(version, task.Result, boundsResult));
    }

    private static BVH<Triangle>? BuildBvh(IReadOnlyList<IndexTriangle> triangles, IReadOnlyList<Vector3> positions)
    {
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
                ? new BVH<Triangle>(new TriangleAdapter(), worldTriangles)
            : null;
    }

            internal readonly record struct Result(int Version, BVH<Triangle>? Tree, SkinnedMeshBoundsCalculator.Result Bounds)
    {
        public bool HasTree => Tree is not null;

        public static Result Empty(int version)
            => new(version, null, default);
    }
}
