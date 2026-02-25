using System.Numerics;

namespace XREngine.Modeling;

public readonly record struct HalfEdgeData(int Origin, int Destination, int Face, int Next, int Previous, int Opposite);

public enum TopologyValidationSeverity
{
    Error = 0,
    Warning
}

public sealed record TopologyValidationIssue(
    TopologyValidationSeverity Severity,
    string Code,
    string Message,
    int? ElementIndex = null);

public sealed class TopologyValidationReport
{
    private readonly List<TopologyValidationIssue> _issues = [];

    public IReadOnlyList<TopologyValidationIssue> Issues => _issues;
    public bool HasErrors => _issues.Any(x => x.Severity == TopologyValidationSeverity.Error);
    public bool IsValid => !HasErrors;

    public void Add(TopologyValidationIssue issue)
        => _issues.Add(issue);
}

public sealed class HalfEdgeTopology
{
    private readonly List<Vector3> _vertices;
    private readonly List<EditableFaceData> _faces;
    private readonly List<HalfEdgeData> _halfEdges = [];
    private readonly Dictionary<EdgeKey, List<int>> _edgeToFaceIds = [];
    private readonly List<EdgeKey> _edges = [];
    private readonly Dictionary<EdgeKey, List<int>> _edgeToHalfEdges = [];

    public IReadOnlyList<Vector3> Vertices => _vertices;
    public IReadOnlyList<EditableFaceData> Faces => _faces;
    public IReadOnlyList<HalfEdgeData> HalfEdges => _halfEdges;
    public IReadOnlyList<EdgeKey> Edges => _edges;
    public IReadOnlyDictionary<EdgeKey, List<int>> EdgeToFaceIds => _edgeToFaceIds;

    public HalfEdgeTopology(IEnumerable<Vector3> vertices, IEnumerable<EditableFaceData> faces)
    {
        _vertices = [.. vertices];
        _faces = [.. faces];
        Rebuild();
    }

    public void Reset(IEnumerable<Vector3> vertices, IEnumerable<EditableFaceData> faces)
    {
        List<Vector3> vertexSnapshot = [.. vertices];
        List<EditableFaceData> faceSnapshot = [.. faces];

        _vertices.Clear();
        _vertices.AddRange(vertexSnapshot);

        _faces.Clear();
        _faces.AddRange(faceSnapshot);

        Rebuild();
    }

    public List<int> ExportTriangleIndices()
    {
        List<int> result = new(_faces.Count * 3);
        foreach (EditableFaceData face in _faces)
        {
            result.Add(face.A);
            result.Add(face.B);
            result.Add(face.C);
        }

        return result;
    }

    public Dictionary<int, HashSet<int>> BuildVertexAdjacency()
    {
        Dictionary<int, HashSet<int>> adjacency = new();
        foreach (EdgeKey edge in _edges)
        {
            adjacency.TryAdd(edge.A, []);
            adjacency[edge.A].Add(edge.B);

            adjacency.TryAdd(edge.B, []);
            adjacency[edge.B].Add(edge.A);
        }

        return adjacency;
    }

    public List<EdgeKey> BuildLoopFromEdge(EdgeKey startEdge)
    {
        if (!_edgeToHalfEdges.TryGetValue(startEdge, out List<int>? halfEdgeIndices) || halfEdgeIndices.Count == 0)
            return [];

        int currentHalfEdge = halfEdgeIndices[0];
        HashSet<int> visitedHalfEdges = [];
        List<EdgeKey> loop = [];

        while (visitedHalfEdges.Add(currentHalfEdge))
        {
            HalfEdgeData edge = _halfEdges[currentHalfEdge];
            loop.Add(new EdgeKey(edge.Origin, edge.Destination));

            if (edge.Opposite < 0)
                break;

            int opposite = edge.Opposite;
            HalfEdgeData oppositeEdge = _halfEdges[opposite];
            int across = _halfEdges[oppositeEdge.Next].Next;
            if (across < 0 || across >= _halfEdges.Count)
                break;

            currentHalfEdge = across;
        }

        return [.. loop.Distinct()];
    }

    public TopologyValidationReport Validate()
    {
        TopologyValidationReport report = new();

        for (int faceIndex = 0; faceIndex < _faces.Count; faceIndex++)
        {
            EditableFaceData face = _faces[faceIndex];
            if (face.A < 0 || face.B < 0 || face.C < 0 ||
                face.A >= _vertices.Count || face.B >= _vertices.Count || face.C >= _vertices.Count)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "face_index_out_of_range",
                    $"Face {faceIndex} contains an out-of-range vertex index.",
                    faceIndex));
                continue;
            }

            if (face.A == face.B || face.B == face.C || face.C == face.A)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "degenerate_face_duplicate_index",
                    $"Face {faceIndex} reuses a vertex index.",
                    faceIndex));
            }

            Vector3 pa = _vertices[face.A];
            Vector3 pb = _vertices[face.B];
            Vector3 pc = _vertices[face.C];
            if (Vector3.Cross(pb - pa, pc - pa).LengthSquared() <= float.Epsilon)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "degenerate_face_zero_area",
                    $"Face {faceIndex} has zero area.",
                    faceIndex));
            }
        }

        foreach ((EdgeKey edge, List<int> faceIds) in _edgeToFaceIds)
        {
            if (faceIds.Count > 2)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "non_manifold_edge",
                    $"Edge ({edge.A}, {edge.B}) is shared by {faceIds.Count} faces.",
                    edge.A));
            }
            else if (faceIds.Count == 1)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Warning,
                    "boundary_edge",
                    $"Edge ({edge.A}, {edge.B}) is on a boundary."));
            }
        }

        for (int i = 0; i < _halfEdges.Count; i++)
        {
            HalfEdgeData edge = _halfEdges[i];
            if (edge.Next < 0 || edge.Next >= _halfEdges.Count || edge.Previous < 0 || edge.Previous >= _halfEdges.Count)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "half_edge_link_invalid",
                    $"Half-edge {i} has invalid next/previous links.",
                    i));
                continue;
            }

            HalfEdgeData next = _halfEdges[edge.Next];
            if (next.Origin != edge.Destination)
            {
                report.Add(new TopologyValidationIssue(
                    TopologyValidationSeverity.Error,
                    "half_edge_chain_mismatch",
                    $"Half-edge {i} next chain origin does not match destination.",
                    i));
            }
        }

        return report;
    }

    private void Rebuild()
    {
        _halfEdges.Clear();
        _edgeToFaceIds.Clear();
        _edges.Clear();
        _edgeToHalfEdges.Clear();

        Dictionary<(int Origin, int Destination), int> directedEdgeToHalfEdge = new();

        for (int faceIndex = 0; faceIndex < _faces.Count; faceIndex++)
        {
            EditableFaceData face = _faces[faceIndex];

            int h0 = _halfEdges.Count;
            int h1 = h0 + 1;
            int h2 = h0 + 2;

            _halfEdges.Add(new HalfEdgeData(face.A, face.B, faceIndex, h1, h2, -1));
            _halfEdges.Add(new HalfEdgeData(face.B, face.C, faceIndex, h2, h0, -1));
            _halfEdges.Add(new HalfEdgeData(face.C, face.A, faceIndex, h0, h1, -1));

            directedEdgeToHalfEdge[(face.A, face.B)] = h0;
            directedEdgeToHalfEdge[(face.B, face.C)] = h1;
            directedEdgeToHalfEdge[(face.C, face.A)] = h2;

            RegisterUndirectedEdge(faceIndex, face.A, face.B, h0);
            RegisterUndirectedEdge(faceIndex, face.B, face.C, h1);
            RegisterUndirectedEdge(faceIndex, face.C, face.A, h2);
        }

        for (int i = 0; i < _halfEdges.Count; i++)
        {
            HalfEdgeData edge = _halfEdges[i];
            if (directedEdgeToHalfEdge.TryGetValue((edge.Destination, edge.Origin), out int opposite))
                _halfEdges[i] = edge with { Opposite = opposite };
        }
    }

    private void RegisterUndirectedEdge(int faceIndex, int first, int second, int halfEdgeIndex)
    {
        EdgeKey edge = new(first, second);
        if (!_edgeToFaceIds.TryGetValue(edge, out List<int>? faces))
        {
            faces = [];
            _edgeToFaceIds[edge] = faces;
            _edges.Add(edge);
        }

        if (!faces.Contains(faceIndex))
            faces.Add(faceIndex);

        if (!_edgeToHalfEdges.TryGetValue(edge, out List<int>? halfEdges))
        {
            halfEdges = [];
            _edgeToHalfEdges[edge] = halfEdges;
        }

        halfEdges.Add(halfEdgeIndex);
    }
}
