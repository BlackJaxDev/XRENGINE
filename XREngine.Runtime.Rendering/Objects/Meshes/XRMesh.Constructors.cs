using Extensions;
using System.Numerics;
using System.Linq;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public static XRMesh Create<T>(params T[] prims) where T : VertexPrimitive
     => new(prims);
    public static XRMesh Create<T>(IEnumerable<T> prims) where T : VertexPrimitive
        => new(prims);
    public static XRMesh CreateTriangles(params Vector3[] positions)
        => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
    public static XRMesh CreateTriangles(IEnumerable<Vector3> positions)
        => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
    public static XRMesh CreateLines(params Vector3[] positions)
        => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
    public static XRMesh CreateLinestrip(bool closed, params Vector3[] positions)
        => new(new VertexLineStrip(closed, [.. positions.Select(x => new Vertex(x))]));
    public static XRMesh CreateLines(IEnumerable<Vector3> positions)
        => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
    public static XRMesh CreatePoints(params Vector3[] positions)
        => new(positions.Select(x => new Vertex(x)));
    public static XRMesh CreatePoints(IEnumerable<Vector3> positions)
        => new(positions.Select(x => new Vertex(x)));

    public XRMesh(IEnumerable<Vertex> vertices, List<ushort> triangleIndices)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("XRMesh Triangles Constructor");

        vertices.TryGetNonEnumeratedCount(out int vertexCountHint);
        List<Vertex> triVertices = vertexCountHint > 0 ? new(vertexCountHint) : [];

        int maxColorCount = 0;
        int maxTexCoordCount = 0;
        AABB? bounds = null;
        Matrix4x4? dataTransform = null;

        bool hasNormalAction = false, hasTangentAction = false, hasTexCoordAction = false, hasColorAction = false;

        foreach (var v in vertices)
        {
            bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
            AddVertex(triVertices, v, ref maxTexCoordCount, ref maxColorCount,
                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
        }

        _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
        _triangles = new List<IndexTriangle>(triangleIndices.Count / 3);
        for (int i = 0; i + 2 < triangleIndices.Count; i += 3)
            _triangles.Add(new IndexTriangle(triangleIndices[i], triangleIndices[i + 1], triangleIndices[i + 2]));
        _type = EPrimitiveType.Triangles;
        VertexCount = triVertices.Count;

        DelVertexAction[] vertexActions = CreateVertexActions(
            hasNormalAction,
            hasTangentAction,
            hasTexCoordAction,
            hasColorAction,
            includePositions: true,
            updateBounds: false);

        InitMeshBuffers(hasNormalAction, hasTangentAction, maxColorCount, maxTexCoordCount);

        Vertex[] sourceVertices = [.. triVertices];

        PopulateVertexData(vertexActions, sourceVertices, VertexCount, dataTransform,
            RuntimeRenderingHostServices.Current.PopulateVertexDataInParallel);

        Vertices = sourceVertices;
    }

    public XRMesh(IEnumerable<object?> primitives) : this()
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope("XRMesh Constructor");

        List<Vertex> points = [];
        List<Vertex> lines = [];
        List<Vertex> triangles = [];

        int maxColorCount = 0, maxTexCoordCount = 0;
        AABB? bounds = null;
        Matrix4x4? dataTransform = null;
        bool hasNormalAction = false, hasTangentAction = false, hasTexCoordAction = false, hasColorAction = false;

        foreach (var prim in primitives)
        {
            switch (prim)
            {
                case Vertex v:
                    bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
                    AddVertex(points, v, ref maxTexCoordCount, ref maxColorCount,
                        ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    break;
                case VertexLinePrimitive lp:
                    foreach (var line in lp.ToLines())
                        foreach (var vtx in line.Vertices)
                        {
                            bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                            AddVertex(lines, vtx, ref maxTexCoordCount, ref maxColorCount,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                        }
                    break;
                case VertexLine line:
                    foreach (var vtx in line.Vertices)
                    {
                        bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                        AddVertex(lines, vtx, ref maxTexCoordCount, ref maxColorCount,
                            ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    }
                    break;
                case VertexPolygon poly:
                    foreach (var tri in poly.ToTriangles())
                        foreach (var vtx in tri.Vertices)
                        {
                            bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                            AddVertex(triangles, vtx, ref maxTexCoordCount, ref maxColorCount,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                        }
                    break;
                case null:
                    break;
                default:
                    throw new ArgumentException($"Unsupported mesh primitive type '{prim.GetType().FullName}'.", nameof(primitives));
            }
        }

        _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);

        int count;
        Remapper? remapper;
        Vertex[] sourceList;
        if (triangles.Count > lines.Count && triangles.Count > points.Count)
        {
            _type = EPrimitiveType.Triangles;
            count = triangles.Count;
            sourceList = [.. triangles];
            remapper = SetTriangleIndices(sourceList);
        }
        else if (lines.Count > triangles.Count && lines.Count > points.Count)
        {
            _type = EPrimitiveType.Lines;
            count = lines.Count;
            sourceList = [.. lines];
            remapper = SetLineIndices(sourceList);
        }
        else
        {
            _type = EPrimitiveType.Points;
            count = points.Count;
            sourceList = [.. points];
            remapper = SetPointIndices(sourceList);
        }

        DelVertexAction[] vertexActions = CreateVertexActions(
            hasNormalAction,
            hasTangentAction,
            hasTexCoordAction,
            hasColorAction,
            includePositions: true,
            updateBounds: false);

        int[] firstAppearanceArray;
        if (remapper?.ImplementationTable is null)
        {
            firstAppearanceArray = new int[count];
            firstAppearanceArray.Fill(x => x);
        }
        else
            firstAppearanceArray = remapper.ImplementationTable!;
        VertexCount = firstAppearanceArray.Length;

        InitMeshBuffers(hasNormalAction, hasTangentAction, maxColorCount, maxTexCoordCount);

        PopulateVertexData(
            vertexActions,
            sourceList,
            firstAppearanceArray,
            dataTransform,
            RuntimeRenderingHostServices.Current.PopulateVertexDataInParallel);

        Vertices = sourceList;
    }
}
