using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Modeling;

/// <summary>
/// Constructive Solid Geometry (CSG) implementation using BSP trees.
/// Operates on indexed triangle meshes represented as positions + triangle indices.
/// 
/// Based on the classic BSP-tree CSG algorithm:
/// each polygon is tagged as coplanar-front, coplanar-back, front, back, or spanning
/// relative to a splitting plane. Spanning polygons are split. The tree is then
/// clipped/inverted to produce boolean results.
/// </summary>
public static class CsgMesh
{
    /// <summary>Tolerance for plane-side classification.</summary>
    private const float Epsilon = 1e-5f;

    #region Public Types

    /// <summary>
    /// Lightweight vertex carrying position and normal through the CSG pipeline.
    /// </summary>
    public struct CsgVertex
    {
        public Vector3 Position;
        public Vector3 Normal;

        public CsgVertex(Vector3 position, Vector3 normal)
        {
            Position = position;
            Normal = normal;
        }

        /// <summary>Linearly interpolates between two vertices.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CsgVertex Lerp(in CsgVertex a, in CsgVertex b, float t)
        {
            Vector3 blended = Vector3.Lerp(a.Normal, b.Normal, t);
            if (blended.LengthSquared() <= Epsilon * Epsilon)
                blended = a.Normal.LengthSquared() > Epsilon * Epsilon ? a.Normal : Vector3.UnitY;
            else
                blended = Vector3.Normalize(blended);

            return new(
                Vector3.Lerp(a.Position, b.Position, t),
                blended);
        }
    }

    /// <summary>
    /// A convex polygon defined by an ordered list of vertices and a supporting plane.
    /// </summary>
    public sealed class CsgPolygon
    {
        public CsgVertex[] Vertices;
        public CsgPlane Plane;

        public CsgPolygon(CsgVertex[] vertices)
        {
            Vertices = vertices;
            Plane = CsgPlane.FromPoints(vertices[0].Position, vertices[1].Position, vertices[2].Position);
        }

        public CsgPolygon(CsgVertex[] vertices, CsgPlane plane)
        {
            Vertices = vertices;
            Plane = plane;
        }

        public CsgPolygon Flipped()
        {
            CsgVertex[] flipped = new CsgVertex[Vertices.Length];
            for (int i = 0; i < Vertices.Length; i++)
            {
                CsgVertex v = Vertices[Vertices.Length - 1 - i];
                v.Normal = -v.Normal;
                flipped[i] = v;
            }
            return new CsgPolygon(flipped, new CsgPlane(-Plane.Normal, -Plane.D));
        }
    }

    /// <summary>
    /// A plane defined by a unit normal and distance from origin.
    /// </summary>
    public readonly struct CsgPlane
    {
        public readonly Vector3 Normal;
        public readonly float D;

        public CsgPlane(Vector3 normal, float d)
        {
            Normal = normal;
            D = d;
        }

        public static CsgPlane FromPoints(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            return new CsgPlane(n, Vector3.Dot(n, a));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float DistanceTo(Vector3 point)
            => Vector3.Dot(Normal, point) - D;
    }

    #endregion

    #region BSP Node

    /// <summary>
    /// A node in a BSP tree. Each node holds a splitting plane, a list of coplanar polygons,
    /// and references to front/back child nodes.
    /// </summary>
    internal sealed class CsgBspNode
    {
        internal CsgPlane? Plane;
        internal List<CsgPolygon> Polygons = [];
        internal CsgBspNode? Front;
        internal CsgBspNode? Back;

        public CsgBspNode() { }

        public CsgBspNode(List<CsgPolygon> polygons)
        {
            Build(polygons);
        }

        /// <summary>Returns a deep copy of this BSP tree.</summary>
        public CsgBspNode Clone()
        {
            CsgBspNode clone = new()
            {
                Plane = Plane,
                Polygons = [.. Polygons],
                Front = Front?.Clone(),
                Back = Back?.Clone()
            };
            return clone;
        }

        /// <summary>Flips all polygons and swaps front/back subtrees, effectively inverting the solid.</summary>
        public void Invert()
        {
            for (int i = 0; i < Polygons.Count; i++)
                Polygons[i] = Polygons[i].Flipped();

            if (Plane.HasValue)
                Plane = new CsgPlane(-Plane.Value.Normal, -Plane.Value.D);

            Front?.Invert();
            Back?.Invert();

            (Front, Back) = (Back, Front);
        }

        /// <summary>
        /// Recursively removes all polygons in <paramref name="polygons"/> that are
        /// inside this BSP tree.
        /// </summary>
        public List<CsgPolygon> ClipPolygons(List<CsgPolygon> polygons)
        {
            if (!Plane.HasValue)
                return [.. polygons];

            List<CsgPolygon> front = [];
            List<CsgPolygon> back = [];

            for (int i = 0; i < polygons.Count; i++)
                SplitPolygon(Plane.Value, polygons[i], front, back, front, back);

            front = Front?.ClipPolygons(front) ?? front;
            back = Back?.ClipPolygons(back) ?? [];

            front.AddRange(back);
            return front;
        }

        /// <summary>
        /// Removes all polygons in this tree that are inside <paramref name="other"/>.
        /// </summary>
        public void ClipTo(CsgBspNode other)
        {
            Polygons = other.ClipPolygons(Polygons);
            Front?.ClipTo(other);
            Back?.ClipTo(other);
        }

        /// <summary>Collects all polygons in this BSP tree.</summary>
        public List<CsgPolygon> AllPolygons()
        {
            List<CsgPolygon> result = [.. Polygons];
            if (Front is not null)
                result.AddRange(Front.AllPolygons());
            if (Back is not null)
                result.AddRange(Back.AllPolygons());
            return result;
        }

        /// <summary>
        /// Builds the BSP tree from the given polygons. If the tree already has
        /// geometry, the new polygons are inserted into the existing tree.
        /// </summary>
        public void Build(List<CsgPolygon> polygons)
        {
            if (polygons.Count == 0)
                return;

            if (!Plane.HasValue)
                Plane = polygons[0].Plane;

            List<CsgPolygon> front = [];
            List<CsgPolygon> back = [];

            for (int i = 0; i < polygons.Count; i++)
                SplitPolygon(Plane.Value, polygons[i], Polygons, Polygons, front, back);

            if (front.Count > 0)
            {
                Front ??= new CsgBspNode();
                Front.Build(front);
            }

            if (back.Count > 0)
            {
                Back ??= new CsgBspNode();
                Back.Build(back);
            }
        }
    }

    #endregion

    #region Polygon Splitting

    private enum PolygonType
    {
        Coplanar = 0,
        Front = 1,
        Back = 2,
        Spanning = 3  // Front | Back
    }

    /// <summary>
    /// Classifies and optionally splits a polygon relative to a plane.
    /// Coplanar polygons are added to <paramref name="coplanarFront"/> or <paramref name="coplanarBack"/>
    /// based on their normal alignment with the plane.
    /// </summary>
    private static void SplitPolygon(
        CsgPlane plane,
        CsgPolygon polygon,
        List<CsgPolygon> coplanarFront,
        List<CsgPolygon> coplanarBack,
        List<CsgPolygon> front,
        List<CsgPolygon> back)
    {
        PolygonType polygonType = 0;
        int vertexCount = polygon.Vertices.Length;

        // Stack-allocate classification array for small polygons, otherwise heap.
        Span<PolygonType> types = vertexCount <= 64
            ? stackalloc PolygonType[vertexCount]
            : new PolygonType[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            float dist = plane.DistanceTo(polygon.Vertices[i].Position);
            PolygonType type = dist < -Epsilon ? PolygonType.Back
                             : dist > Epsilon ? PolygonType.Front
                             : PolygonType.Coplanar;
            polygonType |= type;
            types[i] = type;
        }

        switch (polygonType)
        {
            case PolygonType.Coplanar:
                if (Vector3.Dot(plane.Normal, polygon.Plane.Normal) > 0)
                    coplanarFront.Add(polygon);
                else
                    coplanarBack.Add(polygon);
                break;

            case PolygonType.Front:
                front.Add(polygon);
                break;

            case PolygonType.Back:
                back.Add(polygon);
                break;

            case PolygonType.Spanning:
                List<CsgVertex> f = new(vertexCount + 1);
                List<CsgVertex> b = new(vertexCount + 1);

                for (int i = 0; i < vertexCount; i++)
                {
                    int j = (i + 1) % vertexCount;
                    PolygonType ti = types[i];
                    PolygonType tj = types[j];
                    CsgVertex vi = polygon.Vertices[i];
                    CsgVertex vj = polygon.Vertices[j];

                    if (ti != PolygonType.Back)
                        f.Add(vi);
                    if (ti != PolygonType.Front)
                        b.Add(vi);

                    if ((ti | tj) == PolygonType.Spanning)
                    {
                        float distI = plane.DistanceTo(vi.Position);
                        float distJ = plane.DistanceTo(vj.Position);
                        float t = distI / (distI - distJ);
                        CsgVertex interpolated = CsgVertex.Lerp(in vi, in vj, t);
                        f.Add(interpolated);
                        b.Add(interpolated);
                    }
                }

                if (f.Count >= 3)
                    front.Add(new CsgPolygon([.. f], polygon.Plane));
                if (b.Count >= 3)
                    back.Add(new CsgPolygon([.. b], polygon.Plane));
                break;
        }
    }

    #endregion

    #region Boolean Operations (Internal BSP)

    /// <summary>Union: A ∪ B — returns the combined volume of both meshes.</summary>
    internal static CsgBspNode UnionBsp(CsgBspNode a, CsgBspNode b)
    {
        a = a.Clone();
        b = b.Clone();
        a.ClipTo(b);
        b.ClipTo(a);
        b.Invert();
        b.ClipTo(a);
        b.Invert();
        a.Build(b.AllPolygons());
        return a;
    }

    /// <summary>Intersection: A ∩ B — returns only the volume shared by both meshes.</summary>
    internal static CsgBspNode IntersectBsp(CsgBspNode a, CsgBspNode b)
    {
        a = a.Clone();
        b = b.Clone();
        a.Invert();
        b.ClipTo(a);
        b.Invert();
        a.ClipTo(b);
        b.ClipTo(a);
        a.Build(b.AllPolygons());
        a.Invert();
        return a;
    }

    /// <summary>Difference: A \ B — subtracts B from A.</summary>
    internal static CsgBspNode SubtractBsp(CsgBspNode a, CsgBspNode b)
    {
        a = a.Clone();
        b = b.Clone();
        a.Invert();
        a.ClipTo(b);
        b.ClipTo(a);
        b.Invert();
        b.ClipTo(a);
        b.Invert();
        a.Build(b.AllPolygons());
        a.Invert();
        return a;
    }

    #endregion

    #region Mesh ↔ Polygon Conversion

    /// <summary>
    /// Converts an indexed triangle mesh into a list of CSG polygons.
    /// </summary>
    internal static List<CsgPolygon> MeshToPolygons(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<int> indices,
        Matrix4x4? transform = null)
    {
        int triangleCount = indices.Count / 3;
        List<CsgPolygon> polygons = new(triangleCount);

        for (int i = 0; i < indices.Count; i += 3)
        {
            Vector3 p0 = positions[indices[i]];
            Vector3 p1 = positions[indices[i + 1]];
            Vector3 p2 = positions[indices[i + 2]];

            if (transform.HasValue)
            {
                Matrix4x4 m = transform.Value;
                p0 = Vector3.Transform(p0, m);
                p1 = Vector3.Transform(p1, m);
                p2 = Vector3.Transform(p2, m);
            }

            Vector3 edge1 = p1 - p0;
            Vector3 edge2 = p2 - p0;
            Vector3 normal = Vector3.Cross(edge1, edge2);

            // Skip degenerate triangles.
            if (normal.LengthSquared() < Epsilon * Epsilon)
                continue;

            normal = Vector3.Normalize(normal);

            CsgVertex[] verts =
            [
                new(p0, normal),
                new(p1, normal),
                new(p2, normal)
            ];

            polygons.Add(new CsgPolygon(verts));
        }

        return polygons;
    }

    /// <summary>
    /// Converts CSG polygons back to an indexed triangle mesh.
    /// Polygons with more than 3 vertices are fan-triangulated.
    /// Duplicate vertices are welded using a spatial hash.
    /// </summary>
    internal static (List<Vector3> Positions, List<Vector3> Normals, List<int> Indices) PolygonsToMesh(List<CsgPolygon> polygons)
    {
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<int> indices = [];

        // Spatial welding: use quantized position key to merge coincident vertices.
        const float weldScale = 1e4f;
        Dictionary<long, int> vertexMap = new(polygons.Count * 3);

        int AddOrGetVertex(CsgVertex v)
        {
            // Quantize position for hashing.
            long kx = (long)MathF.Round(v.Position.X * weldScale);
            long ky = (long)MathF.Round(v.Position.Y * weldScale);
            long kz = (long)MathF.Round(v.Position.Z * weldScale);
            long key = kx * 73856093L ^ ky * 19349663L ^ kz * 83492791L;

            // Linear-probe fallback: check existing entry is truly coincident.
            if (vertexMap.TryGetValue(key, out int existing))
            {
                if (Vector3.DistanceSquared(positions[existing], v.Position) < Epsilon * Epsilon)
                    return existing;
            }

            int idx = positions.Count;
            positions.Add(v.Position);
            normals.Add(v.Normal);
            vertexMap[key] = idx;
            return idx;
        }

        foreach (CsgPolygon polygon in polygons)
        {
            if (polygon.Vertices.Length < 3)
                continue;

            int first = AddOrGetVertex(polygon.Vertices[0]);
            for (int i = 1; i < polygon.Vertices.Length - 1; i++)
            {
                int second = AddOrGetVertex(polygon.Vertices[i]);
                int third = AddOrGetVertex(polygon.Vertices[i + 1]);

                // Skip degenerate triangles.
                if (first == second || second == third || third == first)
                    continue;

                indices.Add(first);
                indices.Add(second);
                indices.Add(third);
            }
        }

        return (positions, normals, indices);
    }

    #endregion

    #region Public Entry Points

    /// <summary>
    /// Performs a CSG boolean operation between two indexed triangle meshes.
    /// </summary>
    /// <param name="positionsA">Vertex positions of mesh A.</param>
    /// <param name="indicesA">Triangle indices of mesh A.</param>
    /// <param name="positionsB">Vertex positions of mesh B.</param>
    /// <param name="indicesB">Triangle indices of mesh B.</param>
    /// <param name="operation">The boolean operation to perform.</param>
    /// <param name="transformA">Optional transform applied to mesh A vertices.</param>
    /// <param name="transformB">Optional transform applied to mesh B vertices.</param>
    /// <returns>The resulting mesh as positions, normals, and triangle indices.</returns>
    public static (List<Vector3> Positions, List<Vector3> Normals, List<int> Indices) Boolean(
        IReadOnlyList<Vector3> positionsA, IReadOnlyList<int> indicesA,
        IReadOnlyList<Vector3> positionsB, IReadOnlyList<int> indicesB,
        EBooleanOperation operation,
        Matrix4x4? transformA = null,
        Matrix4x4? transformB = null)
    {
        List<CsgPolygon> polysA = MeshToPolygons(positionsA, indicesA, transformA);
        List<CsgPolygon> polysB = MeshToPolygons(positionsB, indicesB, transformB);

        CsgBspNode nodeA = new(polysA);
        CsgBspNode nodeB = new(polysB);

        CsgBspNode result = operation switch
        {
            EBooleanOperation.Union => UnionBsp(nodeA, nodeB),
            EBooleanOperation.Intersect => IntersectBsp(nodeA, nodeB),
            EBooleanOperation.Difference => SubtractBsp(nodeA, nodeB),
            EBooleanOperation.SymmetricDifference => SymmetricDifferenceBsp(nodeA, nodeB),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return PolygonsToMesh(result.AllPolygons());
    }

    /// <summary>XOR / Symmetric difference: (A \ B) ∪ (B \ A).</summary>
    private static CsgBspNode SymmetricDifferenceBsp(CsgBspNode a, CsgBspNode b)
    {
        CsgBspNode aMinusB = SubtractBsp(a, b);
        CsgBspNode bMinusA = SubtractBsp(b, a);
        return UnionBsp(aMinusB, bMinusA);
    }

    #endregion
}

/// <summary>
/// The type of boolean operation to perform between two meshes.
/// </summary>
public enum EBooleanOperation
{
    /// <summary>A ∪ B — combined volume of both meshes.</summary>
    Union,

    /// <summary>A ∩ B — only the volume shared by both meshes.</summary>
    Intersect,

    /// <summary>A \ B — mesh A with mesh B's volume removed.</summary>
    Difference,

    /// <summary>(A \ B) ∪ (B \ A) — volume in either mesh but not both.</summary>
    SymmetricDifference
}
