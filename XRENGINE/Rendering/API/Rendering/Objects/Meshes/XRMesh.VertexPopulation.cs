using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private void PopulateVertexData(IEnumerable<DelVertexAction> vertexActions, Vertex[] sourceList, int[]? firstAppearanceArray, Matrix4x4? dataTransform, bool parallel)
    {
        int count = firstAppearanceArray?.Length ?? sourceList.Length;
        using var _ = Engine.Profiler.Start($"PopulateVertexData (remapped): {count} {(parallel ? "parallel" : "sequential")}");
        var actions = vertexActions as DelVertexAction[] ?? [.. vertexActions];

        if (parallel)
        {
            Parallel.For(0, count, i =>
            {
                int x = firstAppearanceArray?[i] ?? i;
                var vtx = sourceList[x];
                for (int j = 0; j < actions.Length; j++)
                    actions[j](this, i, x, vtx, dataTransform);
            });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int x = firstAppearanceArray?[i] ?? i;
                var vtx = sourceList[x];
                for (int j = 0; j < actions.Length; j++)
                    actions[j](this, i, x, vtx, dataTransform);
            }
        }
    }

    private void PopulateVertexData(IEnumerable<DelVertexAction> vertexActions, Vertex[] sourceList, int count, Matrix4x4? dataTransform, bool parallel)
    {
        using var _ = Engine.Profiler.Start($"PopulateVertexData: {count} {(parallel ? "parallel" : "sequential")}");
        var actions = vertexActions as DelVertexAction[] ?? [.. vertexActions];

        if (parallel)
        {
            Parallel.For(0, count, i =>
            {
                var vtx = sourceList[i];
                for (int j = 0; j < actions.Length; j++)
                    actions[j](this, i, i, vtx, dataTransform);
            });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var vtx = sourceList[i];
                for (int j = 0; j < actions.Length; j++)
                    actions[j](this, i, i, vtx, dataTransform);
            }
        }
    }

    private static void AddVertex(
        List<Vertex> vertices,
        Vertex v,
        ConcurrentDictionary<int, DelVertexAction> vertexActions,
        ref int maxTexCoordCount,
        ref int maxColorCount,
        ref bool hasNormalAction,
        ref bool hasTangentAction,
        ref bool hasTexCoordAction,
        ref bool hasColorAction)
    {
        if (v == null) return;
        vertices.Add(v);

        if (v.Normal != null && !hasNormalAction)
            hasNormalAction |= AddNormalAction(vertexActions);
        if (v.Tangent != null && !hasTangentAction)
            hasTangentAction |= AddTangentAction(vertexActions);
        if (v.TextureCoordinateSets != null && v.TextureCoordinateSets.Count > 0 && !hasTexCoordAction)
        {
            Interlocked.Exchange(ref maxTexCoordCount, Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count));
            hasTexCoordAction |= AddTexCoordAction(vertexActions);
        }
        if (v.ColorSets != null && v.ColorSets.Count > 0 && !hasColorAction)
        {
            Interlocked.Exchange(ref maxColorCount, Math.Max(maxColorCount, v.ColorSets.Count));
            hasColorAction |= AddColorAction(vertexActions);
        }
    }

    private static bool AddPositionsAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
    {
        static void Action(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
        {
            Vector3 value = vtx?.Position ?? Vector3.Zero;
            if (dataTransform.HasValue)
                value = Vector3.Transform(value, dataTransform.Value);
            @this.SetPosition((uint)i, value);
            @this.ExpandBounds(value);
        }
        return vertexActions.TryAdd(6, Action);
    }
    private static bool AddNormalAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
    {
        static void Action(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
        {
            Vector3 value = vtx?.Normal ?? Vector3.Zero;
            if (dataTransform.HasValue)
                value = Vector3.TransformNormal(value, dataTransform.Value);
            @this.SetNormal((uint)i, value);
        }
        return vertexActions.TryAdd(1, Action);
    }
    private static bool AddTangentAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
    {
        static void Action(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
        {
            Vector3 value = vtx?.Tangent ?? Vector3.Zero;
            if (dataTransform.HasValue)
                value = Vector3.TransformNormal(value, dataTransform.Value);
            @this.SetTangent((uint)i, value);
        }
        return vertexActions.TryAdd(2, Action);
    }
    private static bool AddTexCoordAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
    {
        static void Action(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
        {
            int count = vtx.TextureCoordinateSets?.Count ?? 0;
            for (int tex = 0; tex < count; tex++)
            {
                var value = vtx.TextureCoordinateSets != null ? vtx.TextureCoordinateSets[tex] : Vector2.Zero;
                @this.SetTexCoord((uint)i, value, (uint)tex);
            }
        }
        return vertexActions.TryAdd(3, Action);
    }
    private static bool AddColorAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
    {
        static void Action(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
        {
            int count = vtx.ColorSets?.Count ?? 0;
            for (int c = 0; c < count; c++)
            {
                var value = vtx.ColorSets != null ? vtx.ColorSets[c] : Vector4.Zero;
                @this.SetColor((uint)i, value, (uint)c);
            }
        }
        return vertexActions.TryAdd(4, Action);
    }

    private void ExpandBounds(Vector3 value)
    {
        try
        {
            _boundsLock.Enter();
            _bounds.ExpandToInclude(value);
        }
        finally
        {
            _boundsLock.Exit();
        }
    }
}