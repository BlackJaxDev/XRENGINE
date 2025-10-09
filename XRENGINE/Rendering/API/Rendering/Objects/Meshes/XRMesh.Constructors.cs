using Extensions;
using System.Collections.Concurrent;
using System.Numerics;
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
        using var _ = Engine.Profiler.Start("XRMesh Triangles Constructor");

        List<Vertex> triVertices = [];
        ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

        int maxColorCount = 0;
        int maxTexCoordCount = 0;
        AABB? bounds = null;
        Matrix4x4? dataTransform = null;

        bool hasNormalAction = false, hasTangentAction = false, hasTexCoordAction = false, hasColorAction = false;

        void Add(Vertex v)
        {
            bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
            AddVertex(triVertices, v, vertexActions, ref maxTexCoordCount, ref maxColorCount,
                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
        }
        vertices.ForEach(Add);

        _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
        _triangles = [.. triangleIndices.SelectEvery(3, x => new IndexTriangle(x[0], x[1], x[2]))];
        _type = EPrimitiveType.Triangles;
        VertexCount = triVertices.Count;

        InitMeshBuffers(vertexActions.ContainsKey(1), vertexActions.ContainsKey(2), maxColorCount, maxTexCoordCount);
        AddPositionsAction(vertexActions);

        PopulateVertexData(vertexActions.Values, [.. triVertices], VertexCount, dataTransform,
            Engine.Rendering.Settings.PopulateVertexDataInParallel);

        Vertices = [.. vertices];
    }

    public XRMesh(IEnumerable<VertexPrimitive> primitives) : this()
    {
        using var _ = Engine.Profiler.Start("XRMesh Constructor");

        List<Vertex> points = [];
        List<Vertex> lines = [];
        List<Vertex> triangles = [];
        ConcurrentDictionary<int, DelVertexAction> vertexActions = [];

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
                    AddVertex(points, v, vertexActions, ref maxTexCoordCount, ref maxColorCount,
                        ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    break;
                case VertexLinePrimitive lp:
                    foreach (var line in lp.ToLines())
                        foreach (var vtx in line.Vertices)
                        {
                            bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                            AddVertex(lines, vtx, vertexActions, ref maxTexCoordCount, ref maxColorCount,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                        }
                    break;
                case VertexLine line:
                    foreach (var vtx in line.Vertices)
                    {
                        bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                        AddVertex(lines, vtx, vertexActions, ref maxTexCoordCount, ref maxColorCount,
                            ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                    }
                    break;
                case VertexPolygon poly:
                    foreach (var tri in poly.ToTriangles())
                        foreach (var vtx in tri.Vertices)
                        {
                            bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
                            AddVertex(triangles, vtx, vertexActions, ref maxTexCoordCount, ref maxColorCount,
                                ref hasNormalAction, ref hasTangentAction, ref hasTexCoordAction, ref hasColorAction);
                        }
                    break;
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

        int[] firstAppearanceArray;
        if (remapper?.ImplementationTable is null)
        {
            firstAppearanceArray = new int[count];
            firstAppearanceArray.Fill(x => x);
        }
        else
            firstAppearanceArray = remapper.ImplementationTable!;
        VertexCount = firstAppearanceArray.Length;

        InitMeshBuffers(vertexActions.ContainsKey(1), vertexActions.ContainsKey(2), maxColorCount, maxTexCoordCount);
        AddPositionsAction(vertexActions);

        PopulateVertexData(
            vertexActions.Values,
            sourceList,
            firstAppearanceArray,
            dataTransform,
            Engine.Rendering.Settings.PopulateVertexDataInParallel);

        Vertices = sourceList;
    }
}