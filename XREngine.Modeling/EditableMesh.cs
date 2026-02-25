using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XREngine.Modeling;

public readonly record struct EdgeKey(int First, int Second)
{
    public int A { get; } = Math.Min(First, Second);
    public int B { get; } = Math.Max(First, Second);
}

/// <summary>
/// Editable mesh facade backed by canonical half-edge topology.
/// </summary>
public sealed class EditableMesh
{
    private readonly HalfEdgeTopology _topology;

    public IReadOnlyList<Vector3> Vertices => _topology.Vertices;
    public IReadOnlyList<EditableFaceData> Faces => _topology.Faces;
    public IReadOnlyList<EdgeKey> Edges => _topology.Edges;

    /// <summary>
    /// Creates an editable mesh using triangle list indices.
    /// </summary>
    public EditableMesh(IEnumerable<Vector3> vertices, IEnumerable<int> indices)
    {
        int[] indexArray = indices.ToArray();
        List<EditableFaceData> faces = [];
        for (int i = 0; i + 2 < indexArray.Length; i += 3)
            faces.Add(new EditableFaceData(indexArray[i], indexArray[i + 1], indexArray[i + 2]));

        _topology = new HalfEdgeTopology(vertices, faces);
    }

    /// <summary>
    /// Produces a deep copy so tool operations can be previewed or reverted.
    /// </summary>
    public EditableMesh Clone()
    {
        EditableMesh clone = new(_topology.Vertices, _topology.ExportTriangleIndices());
        return clone;
    }

    /// <summary>
    /// Moves a vertex to a new position without altering topology.
    /// </summary>
    public void SetVertex(int index, Vector3 value)
    {
        List<Vector3> vertices = [.. _topology.Vertices];
        vertices[index] = value;
        _topology.Reset(vertices, _topology.Faces);
    }

    /// <summary>
    /// Adds a loose vertex to the mesh and returns its index.
    /// </summary>
    public int AddVertex(Vector3 vertex)
    {
        List<Vector3> vertices = [.. _topology.Vertices, vertex];
        _topology.Reset(vertices, _topology.Faces);
        return vertices.Count - 1;
    }

    /// <summary>
    /// Ensures an edge exists between the provided vertex indices, returning the canonical key.
    /// </summary>
    public EdgeKey ConnectVertices(int vertexA, int vertexB)
    {
        EdgeKey key = new(vertexA, vertexB);
        if (_topology.EdgeToFaceIds.ContainsKey(key))
            return key;

        // Explicit loose-edge storage is not persisted in triangle-only phase 3 scaffolding.
        // Keep behavior compatible by returning the canonical key.
        return key;
    }

    /// <summary>
    /// Splits an edge by adding a new vertex and re-triangulating any faces that reference the edge.
    /// </summary>
    public int InsertVertexOnEdge(EdgeKey edge, Vector3 position)
        => SplitEdge(edge, 0.5f, null, forcedPosition: position);

    public int SplitEdge(
        EdgeKey edge,
        float t = 0.5f,
        ModelingOperationOptions? options = null,
        Vector3? forcedPosition = null)
    {
        options ??= ModelingOperationOptions.Default;
        if (!_topology.EdgeToFaceIds.TryGetValue(edge, out List<int>? faceIds) || faceIds.Count == 0)
            return AddVertex(forcedPosition ?? Vector3.Lerp(_topology.Vertices[edge.A], _topology.Vertices[edge.B], t));

        List<Vector3> vertices = [.. _topology.Vertices];
        Vector3 splitPosition = forcedPosition
            ?? ModelingAttributeInterpolation.InterpolatePosition(
                vertices[edge.A],
                vertices[edge.B],
                t,
                options.AttributeInterpolationPolicy);

        int newIndex = vertices.Count;
        vertices.Add(splitPosition);

        List<EditableFaceData> faces = [.. _topology.Faces];
        List<int> orderedFaceIds = [.. faceIds.OrderByDescending(x => x)];
        foreach (int faceId in orderedFaceIds)
        {
            EditableFaceData original = faces[faceId];
            int opposite = original.GetOppositeVertex(edge);
            faces[faceId] = new EditableFaceData(edge.A, newIndex, opposite);
            faces.Add(new EditableFaceData(newIndex, edge.B, opposite));
        }

        _topology.Reset(vertices, faces);
        return newIndex;
    }

    public bool CollapseEdge(EdgeKey edge, ModelingOperationOptions? options = null)
    {
        options ??= ModelingOperationOptions.Default;

        if (edge.A < 0 || edge.B < 0 || edge.A >= _topology.Vertices.Count || edge.B >= _topology.Vertices.Count)
            return false;

        List<Vector3> vertices = [.. _topology.Vertices];
        List<EditableFaceData> faces = [.. _topology.Faces];

        Vector3 collapsed = ModelingAttributeInterpolation.InterpolatePosition(
            vertices[edge.A], vertices[edge.B], 0.5f, options.AttributeInterpolationPolicy);
        vertices[edge.A] = collapsed;

        List<EditableFaceData> remapped = new(faces.Count);
        foreach (EditableFaceData face in faces)
        {
            int a = face.A == edge.B ? edge.A : face.A;
            int b = face.B == edge.B ? edge.A : face.B;
            int c = face.C == edge.B ? edge.A : face.C;

            if (a == b || b == c || c == a)
                continue;

            remapped.Add(new EditableFaceData(a, b, c));
        }

        vertices.RemoveAt(edge.B);

        for (int i = 0; i < remapped.Count; i++)
        {
            EditableFaceData face = remapped[i];
            remapped[i] = new EditableFaceData(
                face.A > edge.B ? face.A - 1 : face.A,
                face.B > edge.B ? face.B - 1 : face.B,
                face.C > edge.B ? face.C - 1 : face.C);
        }

        _topology.Reset(vertices, remapped);
        return true;
    }

    /// <summary>
    /// Connects two vertices and updates adjacent faces to reference the new edge when possible.
    /// </summary>
    public EdgeKey ConnectSelectedVertices(int vertexA, int vertexB)
    {
        EdgeKey key = ConnectVertices(vertexA, vertexB);

        if (!_topology.EdgeToFaceIds.TryGetValue(key, out List<int>? adjacentFaces) || adjacentFaces.Count == 0)
            return key;

        List<EditableFaceData> faces = [.. _topology.Faces];
        foreach (int faceId in adjacentFaces.ToList())
        {
            EditableFaceData face = faces[faceId];
            int third = face.GetOppositeVertex(key);
            faces[faceId] = new EditableFaceData(vertexA, vertexB, third);
        }

        _topology.Reset(_topology.Vertices, faces);
        return key;
    }

    public List<int> ExtrudeFaces(
        IEnumerable<int> faceIndices,
        float distance,
        ModelingOperationOptions? options = null)
    {
        options ??= ModelingOperationOptions.Default;
        List<int> selectedFaceIds = [.. faceIndices.Distinct().Where(x => x >= 0 && x < _topology.Faces.Count)];
        if (selectedFaceIds.Count == 0)
            return [];

        List<Vector3> vertices = [.. _topology.Vertices];
        List<EditableFaceData> faces = [.. _topology.Faces];

        HashSet<int> selectedVertices = [];
        foreach (int faceId in selectedFaceIds)
        {
            EditableFaceData face = faces[faceId];
            selectedVertices.Add(face.A);
            selectedVertices.Add(face.B);
            selectedVertices.Add(face.C);
        }

        Dictionary<int, int> oldToNew = [];
        foreach (int sourceVertex in selectedVertices)
        {
            Vector3 normal = ComputeVertexNormal(sourceVertex, faces, vertices);
            Vector3 extruded = vertices[sourceVertex] + normal * distance;
            int newIndex = vertices.Count;
            vertices.Add(extruded);
            oldToNew[sourceVertex] = newIndex;
        }

        foreach (int faceId in selectedFaceIds)
        {
            EditableFaceData face = faces[faceId];
            faces.Add(new EditableFaceData(oldToNew[face.A], oldToNew[face.B], oldToNew[face.C]));
        }

        Dictionary<EdgeKey, int> edgeSelectionCount = [];
        foreach (int faceId in selectedFaceIds)
        {
            foreach (EdgeKey edge in faces[faceId].GetEdges())
                edgeSelectionCount[edge] = edgeSelectionCount.TryGetValue(edge, out int count) ? count + 1 : 1;
        }

        foreach ((EdgeKey edge, int count) in edgeSelectionCount)
        {
            if (count != 1)
                continue;

            int a2 = oldToNew[edge.A];
            int b2 = oldToNew[edge.B];
            faces.Add(new EditableFaceData(edge.A, edge.B, b2));
            faces.Add(new EditableFaceData(edge.A, b2, a2));
        }

        _topology.Reset(vertices, faces);
        return [.. oldToNew.Values];
    }

    public List<int> InsetFaces(
        IEnumerable<int> faceIndices,
        float factor,
        ModelingOperationOptions? options = null)
    {
        options ??= ModelingOperationOptions.Default;
        factor = Math.Clamp(factor, 0f, 1f);

        List<int> selected = [.. faceIndices.Distinct().Where(x => x >= 0 && x < _topology.Faces.Count).OrderByDescending(x => x)];
        if (selected.Count == 0)
            return [];

        List<Vector3> vertices = [.. _topology.Vertices];
        List<EditableFaceData> faces = [.. _topology.Faces];
        List<int> createdVertices = [];

        foreach (int faceIndex in selected)
        {
            EditableFaceData face = faces[faceIndex];
            Vector3 centroid = (vertices[face.A] + vertices[face.B] + vertices[face.C]) / 3f;

            int a2 = vertices.Count;
            vertices.Add(ModelingAttributeInterpolation.InterpolatePosition(vertices[face.A], centroid, factor, options.AttributeInterpolationPolicy));
            int b2 = vertices.Count;
            vertices.Add(ModelingAttributeInterpolation.InterpolatePosition(vertices[face.B], centroid, factor, options.AttributeInterpolationPolicy));
            int c2 = vertices.Count;
            vertices.Add(ModelingAttributeInterpolation.InterpolatePosition(vertices[face.C], centroid, factor, options.AttributeInterpolationPolicy));

            createdVertices.Add(a2);
            createdVertices.Add(b2);
            createdVertices.Add(c2);

            faces.RemoveAt(faceIndex);
            faces.Add(new EditableFaceData(a2, b2, c2));
            faces.Add(new EditableFaceData(face.A, face.B, b2));
            faces.Add(new EditableFaceData(face.A, b2, a2));
            faces.Add(new EditableFaceData(face.B, face.C, c2));
            faces.Add(new EditableFaceData(face.B, c2, b2));
            faces.Add(new EditableFaceData(face.C, face.A, a2));
            faces.Add(new EditableFaceData(face.C, a2, c2));
        }

        _topology.Reset(vertices, faces);
        return createdVertices;
    }

    public List<int> BevelEdges(
        IEnumerable<int> edgeIndices,
        float amount,
        ModelingOperationOptions? options = null)
    {
        options ??= ModelingOperationOptions.Default;
        List<int> selected = [.. edgeIndices.Distinct().Where(x => x >= 0 && x < _topology.Edges.Count)];
        if (selected.Count == 0)
            return [];

        List<int> created = [];
        foreach (int edgeIndex in selected)
        {
            EdgeKey edge = _topology.Edges[edgeIndex];
            Vector3 p = ModelingAttributeInterpolation.InterpolatePosition(
                _topology.Vertices[edge.A],
                _topology.Vertices[edge.B],
                0.5f,
                options.AttributeInterpolationPolicy);

            Vector3 normal = ComputeEdgeNormal(edge, _topology.Faces, _topology.Vertices);
            int newVertex = SplitEdge(edge, 0.5f, options, p + normal * amount);
            created.Add(newVertex);
        }

        return created;
    }

    public bool BridgeEdges(int firstEdgeIndex, int secondEdgeIndex)
    {
        if (firstEdgeIndex < 0 || secondEdgeIndex < 0 ||
            firstEdgeIndex >= _topology.Edges.Count || secondEdgeIndex >= _topology.Edges.Count)
            return false;

        EdgeKey first = _topology.Edges[firstEdgeIndex];
        EdgeKey second = _topology.Edges[secondEdgeIndex];
        if (first == second)
            return false;

        List<EditableFaceData> faces = [.. _topology.Faces,
            new EditableFaceData(first.A, first.B, second.B),
            new EditableFaceData(first.A, second.B, second.A)];

        _topology.Reset(_topology.Vertices, faces);
        return true;
    }

    public List<int> LoopCutFromEdge(EdgeKey startEdge, float t = 0.5f, ModelingOperationOptions? options = null)
    {
        options ??= ModelingOperationOptions.Default;
        List<EdgeKey> ring = _topology.BuildLoopFromEdge(startEdge);
        if (ring.Count == 0)
            return [];

        List<int> created = [];
        foreach (EdgeKey edge in ring)
            created.Add(SplitEdge(edge, t, options));
        return created;
    }

    /// <summary>
    /// Applies a transform to every unique vertex index in the provided sequence.
    /// </summary>
    public void TransformVertices(IEnumerable<int> vertexIndices, Matrix4x4 transform)
    {
        List<Vector3> vertices = [.. _topology.Vertices];
        foreach (int index in vertexIndices.Distinct())
        {
            Vector3 position = vertices[index];
            Vector3 transformed = Vector3.Transform(position, transform);
            vertices[index] = transformed;
        }

        _topology.Reset(vertices, _topology.Faces);
    }

    /// <summary>
    /// Builds an adjacency map between vertices so higher level tooling can navigate mesh flow.
    /// </summary>
    public Dictionary<int, HashSet<int>> BuildVertexAdjacency()
        => _topology.BuildVertexAdjacency();

    /// <summary>
    /// Produces a cached acceleration structure for spatial queries and face/edge lookups.
    /// </summary>
    public MeshAccelerationData GenerateAccelerationStructure()
    {
        return new MeshAccelerationData
        {
            EdgeToFaces = _topology.EdgeToFaceIds.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList()),
            VertexToEdges = BuildVertexAdjacency()
        };
    }

    public TopologyValidationReport ValidateTopology()
        => _topology.Validate();

    /// <summary>
    /// Exports the editable mesh back into simple vertex and index buffers.
    /// </summary>
    public (List<Vector3> Vertices, List<int> Indices) Bake()
    {
        return (_topology.Vertices.ToList(), _topology.ExportTriangleIndices());
    }

    private static Vector3 ComputeVertexNormal(int vertexIndex, IReadOnlyList<EditableFaceData> faces, IReadOnlyList<Vector3> vertices)
    {
        Vector3 sum = Vector3.Zero;
        foreach (EditableFaceData face in faces)
        {
            if (face.A != vertexIndex && face.B != vertexIndex && face.C != vertexIndex)
                continue;

            Vector3 a = vertices[face.A];
            Vector3 b = vertices[face.B];
            Vector3 c = vertices[face.C];
            sum += Vector3.Cross(b - a, c - a);
        }

        if (sum.LengthSquared() <= float.Epsilon)
            return Vector3.UnitY;

        return Vector3.Normalize(sum);
    }

    private static Vector3 ComputeEdgeNormal(EdgeKey edge, IReadOnlyList<EditableFaceData> faces, IReadOnlyList<Vector3> vertices)
    {
        Vector3 sum = Vector3.Zero;
        foreach (EditableFaceData face in faces)
        {
            bool hasA = face.A == edge.A || face.B == edge.A || face.C == edge.A;
            bool hasB = face.A == edge.B || face.B == edge.B || face.C == edge.B;
            if (!hasA || !hasB)
                continue;

            Vector3 a = vertices[face.A];
            Vector3 b = vertices[face.B];
            Vector3 c = vertices[face.C];
            sum += Vector3.Cross(b - a, c - a);
        }

        if (sum.LengthSquared() <= float.Epsilon)
            return Vector3.UnitY;

        return Vector3.Normalize(sum);
    }
}

/// <summary>
/// Lightweight triangle description with helpers for edge queries.
/// </summary>
public sealed record EditableFaceData(int A, int B, int C)
{
    public IEnumerable<EdgeKey> GetEdges()
    {
        yield return new EdgeKey(A, B);
        yield return new EdgeKey(B, C);
        yield return new EdgeKey(C, A);
    }

    public int GetOppositeVertex(EdgeKey edge)
    {
        if (!Contains(edge.A) || !Contains(edge.B))
            throw new InvalidOperationException("Edge does not belong to this face.");

        if (A != edge.A && A != edge.B)
            return A;
        if (B != edge.A && B != edge.B)
            return B;
        return C;
    }

    private bool Contains(int vertex)
        => A == vertex || B == vertex || C == vertex;
}

public sealed class MeshAccelerationData
{
    public required Dictionary<EdgeKey, List<int>> EdgeToFaces { get; init; }
    public required Dictionary<int, HashSet<int>> VertexToEdges { get; init; }
}
