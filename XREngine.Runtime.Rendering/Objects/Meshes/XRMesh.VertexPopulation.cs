using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private static readonly DelVertexAction PositionOnlyAction = WritePosition;
    private static readonly DelVertexAction PositionAndBoundsAction = WritePositionAndBounds;
    private static readonly DelVertexAction NormalAction = WriteNormal;
    private static readonly DelVertexAction TangentAction = WriteTangent;
    private static readonly DelVertexAction TexCoordAction = WriteTexCoord;
    private static readonly DelVertexAction ColorAction = WriteColor;

    private static DelVertexAction[] CreateVertexActions(
        bool hasNormalAction,
        bool hasTangentAction,
        bool hasTexCoordAction,
        bool hasColorAction,
        bool includePositions,
        bool updateBounds)
    {
        int actionCount = (hasNormalAction ? 1 : 0)
            + (hasTangentAction ? 1 : 0)
            + (hasTexCoordAction ? 1 : 0)
            + (hasColorAction ? 1 : 0)
            + (includePositions ? 1 : 0);

        DelVertexAction[] actions = new DelVertexAction[actionCount];
        int index = 0;

        if (hasNormalAction)
            actions[index++] = NormalAction;
        if (hasTangentAction)
            actions[index++] = TangentAction;
        if (hasTexCoordAction)
            actions[index++] = TexCoordAction;
        if (hasColorAction)
            actions[index++] = ColorAction;
        if (includePositions)
            actions[index] = updateBounds ? PositionAndBoundsAction : PositionOnlyAction;

        return actions;
    }

    private void PopulateVertexData(IEnumerable<DelVertexAction> vertexActions, Vertex[] sourceList, int[]? firstAppearanceArray, Matrix4x4? dataTransform, bool parallel)
    {
        int count = firstAppearanceArray?.Length ?? sourceList.Length;
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("PopulateVertexData (remapped)");
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
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("PopulateVertexData");
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

    /// <remarks>
    /// Once a feature flag (e.g. hasNormalAction) is set, further vertices skip that check entirely.
    /// This means maxTexCoordCount / maxColorCount capture the count from the *first* vertex that
    /// has those attributes rather than scanning every vertex for the true max. In practice this is
    /// safe because all real-world exporters (glTF, FBX, Blender, etc.) emit uniform attribute
    /// counts across every vertex in a mesh. The Assimp concurrent path uses the same first-wins
    /// pattern via ConcurrentDictionary.TryAdd.
    /// </remarks>
    private static void AddVertex(
        List<Vertex> vertices,
        Vertex v,
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
            hasNormalAction = true;
        if (v.Tangent != null && !hasTangentAction)
            hasTangentAction = true;
        if (v.TextureCoordinateSets != null && v.TextureCoordinateSets.Count > 0 && !hasTexCoordAction)
        {
            maxTexCoordCount = Math.Max(maxTexCoordCount, v.TextureCoordinateSets.Count);
            hasTexCoordAction = true;
        }
        if (v.ColorSets != null && v.ColorSets.Count > 0 && !hasColorAction)
        {
            maxColorCount = Math.Max(maxColorCount, v.ColorSets.Count);
            hasColorAction = true;
        }
    }

    private static bool AddPositionsAction(ConcurrentDictionary<int, DelVertexAction> vertexActions, bool updateBounds = true)
        => vertexActions.TryAdd(6, updateBounds ? PositionAndBoundsAction : PositionOnlyAction);

    private static void WritePosition(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        Vector3 value = vtx?.Position ?? Vector3.Zero;
        if (dataTransform.HasValue)
            value = Vector3.Transform(value, dataTransform.Value);
        @this.SetPosition((uint)i, value);
    }

    private static void WritePositionAndBounds(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        Vector3 value = vtx?.Position ?? Vector3.Zero;
        if (dataTransform.HasValue)
            value = Vector3.Transform(value, dataTransform.Value);
        @this.SetPosition((uint)i, value);
        @this.ExpandBounds(value);
    }

    private static bool AddNormalAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        => vertexActions.TryAdd(1, NormalAction);

    private static void WriteNormal(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        Vector3 value = vtx?.Normal ?? Vector3.Zero;
        if (dataTransform.HasValue)
            value = Vector3.TransformNormal(value, dataTransform.Value);
        @this.SetNormal((uint)i, value);
    }

    private static bool AddTangentAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        => vertexActions.TryAdd(2, TangentAction);

    private static void WriteTangent(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        Vector3 value = vtx?.Tangent ?? Vector3.Zero;
        if (dataTransform.HasValue)
            value = Vector3.TransformNormal(value, dataTransform.Value);
        float sign = vtx?.BitangentSign ?? 1.0f;
        @this.SetTangent((uint)i, value, sign);
    }

    private static bool AddTexCoordAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        => vertexActions.TryAdd(3, TexCoordAction);

    private static void WriteTexCoord(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        int count = vtx.TextureCoordinateSets?.Count ?? 0;
        for (int tex = 0; tex < count; tex++)
        {
            var value = vtx.TextureCoordinateSets != null ? vtx.TextureCoordinateSets[tex] : Vector2.Zero;
            @this.SetTexCoord((uint)i, value, (uint)tex);
        }
    }

    private static bool AddColorAction(ConcurrentDictionary<int, DelVertexAction> vertexActions)
        => vertexActions.TryAdd(4, ColorAction);

    private static void WriteColor(XRMesh @this, int i, int _, Vertex vtx, Matrix4x4? dataTransform)
    {
        int count = vtx.ColorSets?.Count ?? 0;
        for (int c = 0; c < count; c++)
        {
            var value = vtx.ColorSets != null ? vtx.ColorSets[c] : Vector4.Zero;
            @this.SetColor((uint)i, value, (uint)c);
        }
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
