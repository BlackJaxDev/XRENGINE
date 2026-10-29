using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;

namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>
/// Finds the nearest triangle that a ray hits. The build is deterministic: each node splits at the median
/// triangle on the longest axis of the centroid bounds, and equal centroids keep the order of the triangle index.
/// A triangle that has no area is not in the tree. A triangle hit is from either side.
/// </summary>
public sealed class TriangleBvh
{
    private const int MaxLeafTriangles = 4;
    private const float ParallelEpsilon = 1e-12f;

    private readonly float[] _positions;
    private readonly uint[] _indices;
    private readonly int[] _order;
    private readonly Node[] _nodes;
    private int _nodeCount;

    private struct Node
    {
        public Vector3 Min;
        public Vector3 Max;
        public int Left;
        public int Right;
        public int Start;
        public int Count;
    }

    /// <summary>Builds the tree for the triangles of <paramref name="surface"/>.</summary>
    public TriangleBvh(DeformedSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _positions = surface.Positions;
        _indices = surface.Indices;

        int triangleCount = surface.TriangleCount;
        List<int> valid = new(triangleCount);
        for (int i = 0; i < triangleCount; i++)
        {
            Read(i, out Vector3 a, out Vector3 b, out Vector3 c);
            if (Vector3.Cross(b - a, c - a).LengthSquared() > 0.0f)
                valid.Add(i);
        }

        _order = [.. valid];
        _nodes = new Node[Math.Max(1, 2 * _order.Length)];
        if (_order.Length > 0)
            Build(0, _order.Length);
    }

    /// <summary>
    /// Finds the nearest hit of a ray. The hit distance is in units of <paramref name="direction"/>, so a unit
    /// direction gives a distance in meters.
    /// </summary>
    /// <param name="origin">The start of the ray.</param>
    /// <param name="direction">The direction of the ray.</param>
    /// <param name="maxDistance">The largest hit distance to accept.</param>
    /// <param name="hitDistance">The distance of the nearest hit, in the range (0, <paramref name="maxDistance"/>].</param>
    /// <returns>True when the ray hits a triangle.</returns>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float hitDistance)
    {
        hitDistance = 0.0f;
        if (_nodeCount == 0 || !(maxDistance > 0.0f))
            return false;

        Vector3 inverse = new(1.0f / direction.X, 1.0f / direction.Y, 1.0f / direction.Z);
        float best = maxDistance;
        bool hit = false;
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;

        while (top > 0)
        {
            ref readonly Node node = ref _nodes[stack[--top]];
            if (!IntersectsBox(origin, inverse, node.Min, node.Max, best))
                continue;

            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    Read(_order[i], out Vector3 a, out Vector3 b, out Vector3 c);
                    if (IntersectTriangle(origin, direction, a, b, c, out float t) && t > 0.0f && t <= best)
                    {
                        best = t;
                        hit = true;
                    }
                }
            }
            else
            {
                stack[top++] = node.Right;
                stack[top++] = node.Left;
            }
        }

        if (hit)
            hitDistance = best;
        return hit;
    }

    private void Read(int triangle, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = Vertex(_indices[3 * triangle]);
        b = Vertex(_indices[3 * triangle + 1]);
        c = Vertex(_indices[3 * triangle + 2]);
    }

    private Vector3 Vertex(uint index)
    {
        int offset = 3 * (int)index;
        return new Vector3(_positions[offset], _positions[offset + 1], _positions[offset + 2]);
    }

    private int Build(int start, int count)
    {
        int nodeIndex = _nodeCount++;
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        Vector3 centroidMin = new(float.MaxValue);
        Vector3 centroidMax = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            Read(_order[i], out Vector3 a, out Vector3 b, out Vector3 c);
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
            Vector3 centroid = (a + b + c) * (1.0f / 3.0f);
            centroidMin = Vector3.Min(centroidMin, centroid);
            centroidMax = Vector3.Max(centroidMax, centroid);
        }

        _nodes[nodeIndex].Min = min;
        _nodes[nodeIndex].Max = max;

        if (count <= MaxLeafTriangles)
        {
            _nodes[nodeIndex].Start = start;
            _nodes[nodeIndex].Count = count;
            return nodeIndex;
        }

        Vector3 extent = centroidMax - centroidMin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float[] keys = new float[count];
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            int triangle = _order[start + i];
            Read(triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            keys[i] = ((a + b + c) * (1.0f / 3.0f))[axis];
            ids[i] = triangle;
        }

        // The triangle index breaks ties, so the order does not depend on the sort algorithm.
        Array.Sort(keys, ids, 0, count);
        SortTies(keys, ids, count);
        Array.Copy(ids, 0, _order, start, count);

        int half = count / 2;
        int left = Build(start, half);
        int right = Build(start + half, count - half);
        _nodes[nodeIndex].Left = left;
        _nodes[nodeIndex].Right = right;
        _nodes[nodeIndex].Count = 0;
        return nodeIndex;
    }

    private static void SortTies(float[] keys, int[] ids, int count)
    {
        int runStart = 0;
        for (int i = 1; i <= count; i++)
        {
            if (i < count && keys[i] == keys[runStart])
                continue;
            if (i - runStart > 1)
                Array.Sort(ids, runStart, i - runStart);
            runStart = i;
        }
    }

    private static bool IntersectsBox(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float maxDistance)
    {
        float tMin = 0.0f;
        float tMax = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis];
            float inv = inverse[axis];
            float t0 = (min[axis] - o) * inv;
            float t1 = (max[axis] - o) * inv;
            if (float.IsNaN(t0) || float.IsNaN(t1))
            {
                // The ray is parallel to the slab and lies on its boundary.
                if (o < min[axis] || o > max[axis])
                    return false;
                continue;
            }

            if (t0 > t1)
                (t0, t1) = (t1, t0);
            if (t0 > tMin)
                tMin = t0;
            if (t1 < tMax)
                tMax = t1;
            if (tMin > tMax)
                return false;
        }

        return true;
    }

    /// <summary>Tests a ray against one triangle with the Moller-Trumbore method.</summary>
    private static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0.0f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < ParallelEpsilon)
            return false;

        float inverseDeterminant = 1.0f / determinant;
        Vector3 s = origin - a;
        float u = Vector3.Dot(s, p) * inverseDeterminant;
        if (u < 0.0f || u > 1.0f)
            return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = Vector3.Dot(direction, q) * inverseDeterminant;
        if (v < 0.0f || u + v > 1.0f)
            return false;

        t = Vector3.Dot(edge2, q) * inverseDeterminant;
        return true;
    }
}
