using System.Numerics;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Sampling;

/// <summary>Places splats on a surface. The result depends only on the surface, the count, and the random seed.</summary>
public static class SurfaceSampler
{
    /// <summary>The smallest triangle area, in square meters, that can receive a splat.</summary>
    public const double MinTriangleArea = 1e-12;

    private const float TangentSigmaFactor = 0.6f;
    private const float NormalSigmaRatio = 0.02f;
    private const float SplatOpacity = 0.95f;

    /// <summary>
    /// Places splats on the surface. The chance of a triangle is its share of the total area.
    /// Degenerate and duplicate triangles get no splats.
    /// </summary>
    /// <param name="rest">The surface at the rest pose.</param>
    /// <param name="count">The number of splats.</param>
    /// <param name="rng">The random generator.</param>
    /// <param name="meshopt">The mesh optimizer that removes the degenerate and duplicate triangles.</param>
    /// <returns>The splats. The array is empty when the surface has no usable triangle.</returns>
    public static BakedSplat[] Sample(DeformedSurface rest, int count, BakeRandom rng, IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(meshopt);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int[] usable = FindUsableTriangles(rest, meshopt);
        if (usable.Length == 0 || count == 0)
            return [];

        double[] cumulative = new double[usable.Length];
        double total = 0.0;
        for (int i = 0; i < usable.Length; i++)
        {
            total += TriangleArea(rest, usable[i]);
            cumulative[i] = total;
        }

        float sigmaTangent = TangentSigmaFactor * MathF.Sqrt((float)(total / count));
        Vector3 scale = new(sigmaTangent, sigmaTangent, NormalSigmaRatio * sigmaTangent);
        BakedSplat[] splats = new BakedSplat[count];
        for (int s = 0; s < count; s++)
        {
            double target = rng.NextFloat01() * total;
            int slot = FindFirstAbove(cumulative, target);
            float u1 = rng.NextFloat01();
            float u2 = rng.NextFloat01();
            splats[s] = Place(rest, usable[slot], u1, u2, scale);
        }

        return splats;
    }

    private static BakedSplat Place(DeformedSurface surface, int triangle, float u1, float u2, Vector3 scale)
    {
        ReadTriangle(surface, triangle, out Vector3 a, out Vector3 b, out Vector3 c);
        float root = MathF.Sqrt(u1);
        float wa = 1.0f - root;
        float wb = root * (1.0f - u2);
        float wc = root * u2;
        Vector3 cross = Vector3.Cross(b - a, c - a);
        float crossLength = cross.Length();
        if (!TriangleFrame.TryCompute(a, b, c, out Quaternion frame))
            frame = Quaternion.Identity;

        return new BakedSplat
        {
            Position = a * wa + b * wb + c * wc,
            Rotation = frame,
            Scale = scale,
            LinearColor = Vector3.Zero,
            Opacity = SplatOpacity,
            Normal = crossLength > 0.0f ? cross / crossLength : Vector3.UnitZ,
            SourceTriangle = triangle,
            IsStatic = false,
        };
    }

    private static int FindFirstAbove(double[] cumulative, double target)
    {
        int low = 0;
        int high = cumulative.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (cumulative[middle] > target)
                high = middle;
            else
                low = middle + 1;
        }

        return low;
    }

    /// <summary>Returns the indices of the surface triangles that stay after the filter and have enough area.</summary>
    private static int[] FindUsableTriangles(DeformedSurface surface, IMeshOptimizerBackend meshopt)
    {
        int triangleCount = surface.TriangleCount;
        if (triangleCount == 0)
            return [];

        uint[] source = surface.Indices.AsSpan(0, triangleCount * 3).ToArray();
        uint[] filtered = new uint[source.Length];
        int filteredCount = meshopt.FilterIndexBuffer(filtered, source, surface.Positions, surface.VertexCount, 12, 12);

        // The filter keeps the order of the triangles that it keeps. A walk over both lists gives the original index of each one.
        List<int> usable = new(filteredCount / 3);
        int next = 0;
        for (int t = 0; t < triangleCount && next < filteredCount; t++)
        {
            if (source[t * 3] != filtered[next]
                || source[t * 3 + 1] != filtered[next + 1]
                || source[t * 3 + 2] != filtered[next + 2])
                continue;

            next += 3;
            if (TriangleArea(surface, t) > MinTriangleArea)
                usable.Add(t);
        }

        if (next != filteredCount)
            throw new InvalidOperationException("The index filter returned triangles that are not in the surface.");

        return [.. usable];
    }

    private static double TriangleArea(DeformedSurface surface, int triangle)
    {
        ReadTriangle(surface, triangle, out Vector3 a, out Vector3 b, out Vector3 c);
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        double x = (double)ab.Y * ac.Z - (double)ab.Z * ac.Y;
        double y = (double)ab.Z * ac.X - (double)ab.X * ac.Z;
        double z = (double)ab.X * ac.Y - (double)ab.Y * ac.X;
        return 0.5 * Math.Sqrt(x * x + y * y + z * z);
    }

    private static void ReadTriangle(DeformedSurface surface, int triangle, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        float[] positions = surface.Positions;
        uint[] indices = surface.Indices;
        a = ReadVertex(positions, indices[triangle * 3]);
        b = ReadVertex(positions, indices[triangle * 3 + 1]);
        c = ReadVertex(positions, indices[triangle * 3 + 2]);
    }

    private static Vector3 ReadVertex(float[] positions, uint vertex)
    {
        int offset = (int)vertex * 3;
        return new Vector3(positions[offset], positions[offset + 1], positions[offset + 2]);
    }
}
