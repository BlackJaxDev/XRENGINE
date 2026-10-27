using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>
/// Finds the triangle that is closest to a point. Only triangles that have a frame
/// (<see cref="TriangleFrame.TryCompute"/> succeeds) are candidates.
/// </summary>
/// <remarks>
/// <para>The grid is uniform. The cell size is two times the mean edge length of the candidates. Each candidate is in
/// every cell that its bounding box touches. Only cells with candidates use memory.</para>
/// <para>A query tests the cell of the point and then rings of cells around it. A cell in ring <c>r</c> is at
/// least <c>(r − 1) × cell size</c> from the point, so the search stops at the first ring that cannot have a
/// closer triangle. When two triangles are at the same distance, the triangle with the lower index wins, so the
/// result does not depend on the order of the search.</para>
/// </remarks>
internal sealed class TriangleGrid
{
    /// <summary>The cell size is increased when the bounds need more cells than this along one axis.</summary>
    private const int MaxCellsPerAxis = 1 << 20;

    /// <summary>A candidate whose bounding box touches more cells than this is tested by every query instead.</summary>
    private const long MaxCellsPerCandidate = 512;

    /// <summary>When the search reaches this ring, it tests every candidate instead.</summary>
    private const int MaxRingsBeforeScan = 8;

    /// <summary>When the mean edge length is zero, the cell size is the bounds diagonal divided by this value.</summary>
    private const float BoundsDiagonalCells = 64.0f;

    private const int KeyBits = 21;

    private readonly Vector3[] _a;
    private readonly Vector3[] _b;
    private readonly Vector3[] _c;
    private readonly uint[] _triangles;
    private readonly int[] _largeCandidates;
    private readonly Dictionary<long, int> _cellSlots;
    private readonly int[] _cellStart;
    private readonly int[] _cellCandidates;
    private readonly Vector3 _origin;
    private readonly float _cellSize;
    private readonly int _dimX;
    private readonly int _dimY;
    private readonly int _dimZ;

    /// <summary>Makes the grid for a triangle list.</summary>
    /// <param name="indices">The triangle list. Each index must be less than the length of <paramref name="positions"/>.</param>
    /// <param name="positions">The position of each vertex.</param>
    public TriangleGrid(ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions)
    {
        int triangleCount = indices.Length / 3;
        List<int> candidates = new(triangleCount);
        double edgeSum = 0.0;
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            Vector3 a = positions[(int)indices[triangle * 3]];
            Vector3 b = positions[(int)indices[triangle * 3 + 1]];
            Vector3 c = positions[(int)indices[triangle * 3 + 2]];
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c) || !TriangleFrame.TryCompute(a, b, c, out _))
                continue;

            candidates.Add(triangle);
            edgeSum += (double)Vector3.Distance(a, b) + Vector3.Distance(b, c) + Vector3.Distance(c, a);
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }

        int count = candidates.Count;
        _a = new Vector3[count];
        _b = new Vector3[count];
        _c = new Vector3[count];
        _triangles = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int triangle = candidates[i];
            _a[i] = positions[(int)indices[triangle * 3]];
            _b[i] = positions[(int)indices[triangle * 3 + 1]];
            _c[i] = positions[(int)indices[triangle * 3 + 2]];
            _triangles[i] = (uint)triangle;
        }

        _cellSlots = [];
        if (count == 0)
        {
            _largeCandidates = [];
            _cellStart = [0];
            _cellCandidates = [];
            _cellSize = 1.0f;
            return;
        }

        Vector3 extent = max - min;
        float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        float cellSize = (float)(2.0 * edgeSum / (3.0 * count));
        if (!(cellSize > 0.0f) || !float.IsFinite(cellSize))
            cellSize = extent.Length() / BoundsDiagonalCells;
        if (!(cellSize > 0.0f) || !float.IsFinite(cellSize))
            cellSize = 1.0f;

        cellSize = MathF.Max(cellSize, maxExtent / MaxCellsPerAxis);
        _origin = min;
        _cellSize = cellSize;
        _dimX = (int)MathF.Floor(extent.X / cellSize) + 1;
        _dimY = (int)MathF.Floor(extent.Y / cellSize) + 1;
        _dimZ = (int)MathF.Floor(extent.Z / cellSize) + 1;
        Fill(count, out _largeCandidates, out _cellStart, out _cellCandidates);
    }

    /// <summary>Gets the number of triangles that have a frame.</summary>
    public int CandidateCount
        => _triangles.Length;

    /// <summary>Finds the closest candidate triangle to the point.</summary>
    /// <param name="point">The point.</param>
    /// <param name="triangle">The index of the closest triangle in the triangle list.</param>
    /// <returns>False when there is no candidate or the point is not finite.</returns>
    public bool TryFindClosest(Vector3 point, out uint triangle)
    {
        int best = -1;
        float bestDistanceSquared = float.PositiveInfinity;
        if (IsFinite(point))
        {
            foreach (int candidate in _largeCandidates)
                Test(point, candidate, ref best, ref bestDistanceSquared);

            if (_cellCandidates.Length != 0)
                SearchRings(point, ref best, ref bestDistanceSquared);
        }

        triangle = best >= 0 ? _triangles[best] : 0u;
        return best >= 0;
    }

    private void SearchRings(Vector3 point, ref int best, ref float bestDistanceSquared)
    {
        float fx = (point.X - _origin.X) / _cellSize;
        float fy = (point.Y - _origin.Y) / _cellSize;
        float fz = (point.Z - _origin.Z) / _cellSize;

        // A point far outside the grid would need many empty rings.
        if (!(fx >= -1.0f && fx < _dimX + 1.0f && fy >= -1.0f && fy < _dimY + 1.0f && fz >= -1.0f && fz < _dimZ + 1.0f))
        {
            ScanAll(point, ref best, ref bestDistanceSquared);
            return;
        }

        int cx = (int)MathF.Floor(fx);
        int cy = (int)MathF.Floor(fy);
        int cz = (int)MathF.Floor(fz);
        int maxRing = Math.Max(Math.Max(Math.Max(cx, _dimX - 1 - cx), Math.Max(cy, _dimY - 1 - cy)), Math.Max(cz, _dimZ - 1 - cz));
        for (int ring = 0; ring <= maxRing; ring++)
        {
            if (best >= 0 && ring > 0)
            {
                float reach = (ring - 1) * _cellSize;
                if (reach * reach > bestDistanceSquared)
                    return;
            }

            if (ring > MaxRingsBeforeScan)
            {
                ScanAll(point, ref best, ref bestDistanceSquared);
                return;
            }

            VisitRing(point, cx, cy, cz, ring, ref best, ref bestDistanceSquared);
        }
    }

    /// <summary>Tests the cells whose largest axis offset from the cell of the point is <paramref name="ring"/>.</summary>
    private void VisitRing(Vector3 point, int cx, int cy, int cz, int ring, ref int best, ref float bestDistanceSquared)
    {
        int x0 = Math.Max(cx - ring, 0);
        int x1 = Math.Min(cx + ring, _dimX - 1);
        int y0 = Math.Max(cy - ring, 0);
        int y1 = Math.Min(cy + ring, _dimY - 1);
        int z0 = Math.Max(cz - ring, 0);
        int z1 = Math.Min(cz + ring, _dimZ - 1);
        for (int x = x0; x <= x1; x++)
        {
            bool onXFace = x == cx - ring || x == cx + ring;
            for (int y = y0; y <= y1; y++)
            {
                if (onXFace || y == cy - ring || y == cy + ring)
                {
                    for (int z = z0; z <= z1; z++)
                        VisitCell(point, x, y, z, ref best, ref bestDistanceSquared);
                    continue;
                }

                if ((uint)(cz - ring) < (uint)_dimZ)
                    VisitCell(point, x, y, cz - ring, ref best, ref bestDistanceSquared);
                if ((uint)(cz + ring) < (uint)_dimZ)
                    VisitCell(point, x, y, cz + ring, ref best, ref bestDistanceSquared);
            }
        }
    }

    private void VisitCell(Vector3 point, int x, int y, int z, ref int best, ref float bestDistanceSquared)
    {
        if (!_cellSlots.TryGetValue(Key(x, y, z), out int slot))
            return;

        int end = _cellStart[slot + 1];
        for (int i = _cellStart[slot]; i < end; i++)
            Test(point, _cellCandidates[i], ref best, ref bestDistanceSquared);
    }

    private void ScanAll(Vector3 point, ref int best, ref float bestDistanceSquared)
    {
        for (int candidate = 0; candidate < _triangles.Length; candidate++)
            Test(point, candidate, ref best, ref bestDistanceSquared);
    }

    private void Test(Vector3 point, int candidate, ref int best, ref float bestDistanceSquared)
    {
        Vector3 closest = ClosestPointOnTriangle(point, _a[candidate], _b[candidate], _c[candidate]);
        float distanceSquared = Vector3.DistanceSquared(point, closest);
        if (distanceSquared < bestDistanceSquared
            || (distanceSquared == bestDistanceSquared && best >= 0 && _triangles[candidate] < _triangles[best]))
        {
            best = candidate;
            bestDistanceSquared = distanceSquared;
        }
    }

    /// <summary>
    /// Puts each candidate in the cells that its bounding box touches, as a compressed list: the candidates of
    /// slot <c>s</c> are from <c>cellStart[s]</c> to <c>cellStart[s + 1] − 1</c>.
    /// </summary>
    private void Fill(int count, out int[] largeCandidates, out int[] cellStart, out int[] cellCandidates)
    {
        List<int> large = [];
        List<int> slotCounts = [];
        for (int candidate = 0; candidate < count; candidate++)
        {
            GetCellRange(candidate, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > MaxCellsPerCandidate)
            {
                large.Add(candidate);
                continue;
            }

            for (int z = z0; z <= z1; z++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        long key = Key(x, y, z);
                        if (!_cellSlots.TryGetValue(key, out int slot))
                        {
                            slot = slotCounts.Count;
                            _cellSlots.Add(key, slot);
                            slotCounts.Add(0);
                        }

                        slotCounts[slot]++;
                    }
                }
            }
        }

        cellStart = new int[slotCounts.Count + 1];
        for (int slot = 0; slot < slotCounts.Count; slot++)
            cellStart[slot + 1] = cellStart[slot] + slotCounts[slot];

        cellCandidates = new int[cellStart[^1]];
        int[] cursor = new int[slotCounts.Count];
        Array.Copy(cellStart, cursor, slotCounts.Count);
        int nextLarge = 0;
        for (int candidate = 0; candidate < count; candidate++)
        {
            if (nextLarge < large.Count && large[nextLarge] == candidate)
            {
                nextLarge++;
                continue;
            }

            GetCellRange(candidate, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
            for (int z = z0; z <= z1; z++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int slot = _cellSlots[Key(x, y, z)];
                        cellCandidates[cursor[slot]++] = candidate;
                    }
                }
            }
        }

        largeCandidates = [.. large];
    }

    private void GetCellRange(int candidate, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
    {
        Vector3 a = _a[candidate];
        Vector3 b = _b[candidate];
        Vector3 c = _c[candidate];
        Vector3 low = Vector3.Min(a, Vector3.Min(b, c));
        Vector3 high = Vector3.Max(a, Vector3.Max(b, c));
        x0 = CellOf(low.X, _origin.X, _dimX);
        y0 = CellOf(low.Y, _origin.Y, _dimY);
        z0 = CellOf(low.Z, _origin.Z, _dimZ);
        x1 = CellOf(high.X, _origin.X, _dimX);
        y1 = CellOf(high.Y, _origin.Y, _dimY);
        z1 = CellOf(high.Z, _origin.Z, _dimZ);
    }

    private int CellOf(float value, float origin, int dimension)
        => Math.Clamp((int)MathF.Floor((value - origin) / _cellSize), 0, dimension - 1);

    /// <summary>Packs the cell coordinates into one key. Each coordinate is from 0 to <see cref="MaxCellsPerAxis"/>, so it fits in <see cref="KeyBits"/> bits.</summary>
    private static long Key(int x, int y, int z)
        => x + ((long)y << KeyBits) + ((long)z << (2 * KeyBits));

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Returns the point of triangle abc that is closest to <paramref name="p"/>, by the Voronoi regions of its corners and edges.</summary>
    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f)
            return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3)
            return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
            return a + d1 / (d1 - d3) * ab;

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6)
            return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
            return a + d2 / (d2 - d6) * ac;

        float va = d3 * d6 - d5 * d4;
        if (va <= 0.0f && d4 - d3 >= 0.0f && d5 - d6 >= 0.0f)
            return b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b);

        float denominator = 1.0f / (va + vb + vc);
        return a + vb * denominator * ab + vc * denominator * ac;
    }
}
