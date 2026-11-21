using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Finds the vertices of skinned meshes that lie on a clothing layer: within
/// <see cref="DefaultDistance"/> of a triangle of another skinned mesh of the same model, in bind
/// pose. The cluster hierarchy cook locks these vertices, so a stocking and the skin under it keep
/// their full detail. Each mesh simplifies on its own, so a simplification error far below one pixel
/// can still exceed the gap between two layers and let the lower layer show through the upper one.
/// </summary>
/// <remarks>
/// Positions are compared in bind space (the mesh positions moved by <see cref="XRMesh.BindRootMatrix"/>
/// when the mesh has one). A uniform grid with cells larger than the lock distance holds every
/// triangle, so each vertex tests only the triangles of the 27 cells around it. This runs once per
/// model at cook time.
/// </remarks>
public static class SkinnedLayerContactLocks
{
    /// <summary>Lock distance in bind-space units (meters for the usual avatar import).</summary>
    public const float DefaultDistance = 0.004f;

    private const float CellSize = 0.005f;
    private const long MaxCellsPerTriangle = 512;
    private const int MaxLargeTriangles = 256;

    /// <summary>
    /// Returns, for each mesh with at least one contact vertex, one byte per vertex (1 = locked).
    /// Meshes without triangles or positions take no part.
    /// </summary>
    public static Dictionary<XRMesh, byte[]> Compute(IReadOnlyList<XRMesh> meshes, float distance = DefaultDistance)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var result = new Dictionary<XRMesh, byte[]>(ReferenceEqualityComparer.Instance);
        if (meshes.Count < 2 || !(distance > 0.0f))
            return result;

        var positions = new Vector3[meshes.Count][];
        var triangles = new int[meshes.Count][];
        var triangleBase = new int[meshes.Count + 1];
        for (int m = 0; m < meshes.Count; m++)
        {
            positions[m] = ReadBindPositions(meshes[m]);
            triangles[m] = positions[m].Length == 0 ? [] : meshes[m].GetIndices(EPrimitiveType.Triangles) ?? [];
            triangleBase[m + 1] = triangleBase[m] + triangles[m].Length / 3;
        }

        // Grid of global triangle ids. A triangle is inserted in every cell that its bounds touch. A
        // triangle whose bounds cover more than MaxCellsPerTriangle cells (for example a stray
        // vertex far from the body) goes to a short list that every query tests instead, so one
        // large triangle cannot fill millions of cells.
        var grid = new Dictionary<(int X, int Y, int Z), List<int>>();
        var large = new List<int>();
        for (int m = 0; m < meshes.Count; m++)
        {
            int[] tri = triangles[m];
            Vector3[] pos = positions[m];
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                if ((uint)tri[t] >= (uint)pos.Length || (uint)tri[t + 1] >= (uint)pos.Length || (uint)tri[t + 2] >= (uint)pos.Length)
                    continue;
                Vector3 a = pos[tri[t]], b = pos[tri[t + 1]], c = pos[tri[t + 2]];
                (int X, int Y, int Z) lo = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                (int X, int Y, int Z) hi = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                int id = triangleBase[m] + t / 3;
                long cellCount = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
                if (cellCount > MaxCellsPerTriangle)
                {
                    large.Add(id);
                    continue;
                }
                for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    if (!grid.TryGetValue((x, y, z), out List<int>? list))
                        grid[(x, y, z)] = list = new List<int>(4);
                    list.Add(id);
                }
            }
        }

        // Too many large triangles would make every query slow; they then take no part.
        if (large.Count > MaxLargeTriangles)
            large.Clear();

        float distanceSquared = distance * distance;

        // True when triangle `id` belongs to another mesh than `mesh` and lies within the lock distance of `p`.
        bool TestTriangle(int id, int mesh, Vector3 p)
        {
            int owner = FindOwner(triangleBase, id);
            if (owner == mesh)
                return false;
            int t = (id - triangleBase[owner]) * 3;
            int[] tri = triangles[owner];
            Vector3[] other = positions[owner];
            return DistanceSquaredToTriangle(p, other[tri[t]], other[tri[t + 1]], other[tri[t + 2]]) <= distanceSquared;
        }

        int[] stamp = new int[triangleBase[meshes.Count]];
        int query = 0;
        for (int m = 0; m < meshes.Count; m++)
        {
            Vector3[] pos = positions[m];
            byte[]? locks = null;
            for (int v = 0; v < pos.Length; v++)
            {
                query++;
                Vector3 p = pos[v];
                (int X, int Y, int Z) center = Cell(p);
                bool hit = false;
                for (int dx = -1; dx <= 1 && !hit; dx++)
                for (int dy = -1; dy <= 1 && !hit; dy++)
                for (int dz = -1; dz <= 1 && !hit; dz++)
                {
                    if (!grid.TryGetValue((center.X + dx, center.Y + dy, center.Z + dz), out List<int>? list))
                        continue;
                    foreach (int id in list)
                    {
                        if (stamp[id] == query)
                            continue;
                        stamp[id] = query;
                        if (TestTriangle(id, m, p))
                        {
                            hit = true;
                            break;
                        }
                    }
                }

                for (int i = 0; i < large.Count && !hit; i++)
                    hit = TestTriangle(large[i], m, p);

                if (hit)
                    (locks ??= new byte[pos.Length])[v] = 1;
            }

            if (locks is not null)
                result[meshes[m]] = locks;
        }

        return result;
    }

    private static Vector3[] ReadBindPositions(XRMesh mesh)
    {
        int count = Math.Max(0, mesh.VertexCount);
        var positions = new Vector3[count];
        Matrix4x4? bindRoot = mesh.BindRootMatrix;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = mesh.GetPosition((uint)i);
            positions[i] = bindRoot is { } root ? Vector3.Transform(p, root) : p;
        }
        return positions;
    }

    private static (int X, int Y, int Z) Cell(Vector3 p)
        => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize), (int)MathF.Floor(p.Z / CellSize));

    private static int FindOwner(int[] triangleBase, int id)
    {
        int lo = 0, hi = triangleBase.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (triangleBase[mid] <= id)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Squared distance from a point to a triangle (closest point by Voronoi regions).</summary>
    private static float DistanceSquaredToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f)
            return ap.LengthSquared();
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3)
            return bp.LengthSquared();
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
        {
            float v = d1 / (d1 - d3);
            return (p - (a + v * ab)).LengthSquared();
        }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6)
            return cp.LengthSquared();
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
        {
            float w = d2 / (d2 - d6);
            return (p - (a + w * ac)).LengthSquared();
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return (p - (b + w * (c - b))).LengthSquared();
        }
        float denominator = 1.0f / (va + vb + vc);
        float vv = vb * denominator, ww = vc * denominator;
        return (p - (a + ab * vv + ac * ww)).LengthSquared();
    }
}
