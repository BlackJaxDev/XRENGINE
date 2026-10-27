using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>Attaches splats to the triangles of one driver level at the rest pose.</summary>
public static class SplatBinder
{
    /// <summary>
    /// Attaches each splat to the closest triangle of the level that has a frame (<see cref="TriangleFrame.TryCompute"/>
    /// succeeds). The binding keeps the plane coordinates of the projection of the splat center onto the triangle
    /// plane without a clamp, and the signed distance from the plane, so that <see cref="TriangleFrame.Reconstruct"/>
    /// gives the rest position again.
    /// </summary>
    /// <remarks>
    /// Give only animated splats. Static splats (<see cref="BakedSplat.IsStatic"/>) do not move, so they keep their
    /// world position and get no binding. A closest-triangle tie goes to the triangle with the lower index.
    /// </remarks>
    /// <param name="splats">The animated splats at the rest pose.</param>
    /// <param name="drivers">The driver hierarchy.</param>
    /// <param name="level">The driver level whose triangles receive the splats.</param>
    /// <param name="driverRestPositions">The rest position of each driver vertex, in coarse-first order (see <see cref="DriverHierarchy.GatherPositions"/>).</param>
    /// <returns>One binding for each splat, in splat order.</returns>
    /// <exception cref="InvalidOperationException">The level has no triangle with a frame.</exception>
    public static SplatBinding[] Bind(ReadOnlySpan<BakedSplat> splats, DriverHierarchy drivers, int level, ReadOnlySpan<Vector3> driverRestPositions)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, drivers.LevelCount);
        if (driverRestPositions.Length < drivers.CumulativeVertexCounts[level])
            throw new ArgumentException("There must be one rest position for each driver vertex of the level.", nameof(driverRestPositions));
        if (splats.IsEmpty)
            return [];

        uint[] indices = drivers.LevelTriangleIndices[level];
        TriangleGrid grid = new(indices, driverRestPositions);
        if (grid.CandidateCount == 0)
            throw new InvalidOperationException($"Driver level {level} has no triangle with a frame, so the splats cannot be bound to it.");

        SplatBinding[] bindings = new SplatBinding[splats.Length];
        for (int i = 0; i < splats.Length; i++)
        {
            ref readonly BakedSplat splat = ref splats[i];
            if (!grid.TryFindClosest(splat.Position, out uint triangle))
                throw new ArgumentException($"Splat {i} has a position that is not finite.", nameof(splats));

            Vector3 a = driverRestPositions[(int)indices[triangle * 3]];
            Vector3 b = driverRestPositions[(int)indices[triangle * 3 + 1]];
            Vector3 c = driverRestPositions[(int)indices[triangle * 3 + 2]];
            TriangleFrame.TryCompute(a, b, c, out Quaternion frame);
            Project(splat.Position, a, b, c, out float u, out float v, out float h);
            bindings[i] = new SplatBinding
            {
                Triangle = triangle,
                U = u,
                V = v,
                H = h,
                LocalRotation = Quaternion.Normalize(Quaternion.Inverse(frame) * splat.Rotation),
            };
        }

        return bindings;
    }

    /// <summary>
    /// Finds u, v, and h such that p = a + u(b − a) + v(c − a) + h·n, where n is the unit normal of the triangle.
    /// The calculation uses double precision, because u and v are not clamped and can be far outside the triangle.
    /// </summary>
    private static void Project(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float h)
    {
        double abX = (double)b.X - a.X, abY = (double)b.Y - a.Y, abZ = (double)b.Z - a.Z;
        double acX = (double)c.X - a.X, acY = (double)c.Y - a.Y, acZ = (double)c.Z - a.Z;
        double apX = (double)p.X - a.X, apY = (double)p.Y - a.Y, apZ = (double)p.Z - a.Z;

        double nX = abY * acZ - abZ * acY;
        double nY = abZ * acX - abX * acZ;
        double nZ = abX * acY - abY * acX;
        double nLength = Math.Sqrt(nX * nX + nY * nY + nZ * nZ);
        h = (float)((apX * nX + apY * nY + apZ * nZ) / nLength);

        // The normal part of ap is perpendicular to both edges, so the dot products give the plane coordinates of the projection.
        double d00 = abX * abX + abY * abY + abZ * abZ;
        double d01 = abX * acX + abY * acY + abZ * acZ;
        double d11 = acX * acX + acY * acY + acZ * acZ;
        double d20 = apX * abX + apY * abY + apZ * abZ;
        double d21 = apX * acX + apY * acY + apZ * acZ;
        double denominator = d00 * d11 - d01 * d01;
        u = (float)((d11 * d20 - d01 * d21) / denominator);
        v = (float)((d00 * d21 - d01 * d20) / denominator);
    }
}
