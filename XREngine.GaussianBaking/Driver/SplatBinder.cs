using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>Attaches splats to the triangles of one driver level at the rest pose.</summary>
public static class SplatBinder
{
    /// <summary>
    /// The smallest <c>|ab × ac| / (longest edge)²</c> of a triangle that can receive splats, when the level has one.
    /// The value is the height on the longest edge divided by that edge.
    /// </summary>
    public const double MinTriangleShapeRatio = 1e-3;

    /// <summary>The largest distance, in meters, between a reconstructed splat center and its rest position.</summary>
    public const float MaxReconstructErrorMeters = 1e-5f;

    /// <summary>
    /// Attaches each splat to the closest candidate triangle of the level. The binding keeps the plane coordinates of
    /// the projection of the splat center onto the triangle plane without a clamp, and the signed distance from the
    /// plane, so that <see cref="TriangleFrame.Reconstruct"/> gives the rest position again.
    /// </summary>
    /// <remarks>
    /// <para>The candidates are the triangles that have a frame (<see cref="TriangleFrame.TryCompute"/> succeeds) and
    /// a shape ratio of at least <see cref="MinTriangleShapeRatio"/>. A sliver is not a candidate, because its plane
    /// coordinates are badly conditioned and its frame turns fast when its vertices move. When no triangle of the level
    /// has that shape, the candidates are all triangles that have a frame.</para>
    /// <para>Give only animated splats. Static splats (<see cref="BakedSplat.IsStatic"/>) do not move, so they keep
    /// their world position and get no binding. A closest-triangle tie goes to the triangle with the lower index.</para>
    /// </remarks>
    /// <param name="splats">The animated splats at the rest pose.</param>
    /// <param name="drivers">The driver hierarchy.</param>
    /// <param name="level">The driver level whose triangles receive the splats.</param>
    /// <param name="driverRestPositions">The rest position of each driver vertex, in coarse-first order (see <see cref="DriverHierarchy.GatherPositions"/>).</param>
    /// <returns>One binding for each splat, in splat order.</returns>
    /// <exception cref="InvalidOperationException">
    /// The level has no triangle with a frame, or a binding is not finite or does not reconstruct the splat center
    /// within <see cref="MaxReconstructErrorMeters"/>. The second case is a defect of the bake, not of the content.
    /// </exception>
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
        TriangleGrid grid = new(indices, driverRestPositions, MinTriangleShapeRatio);
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
            SplatBinding binding = new()
            {
                Triangle = triangle,
                U = u,
                V = v,
                H = h,
                LocalRotation = Quaternion.Normalize(Quaternion.Inverse(frame) * splat.Rotation),
            };
            Verify(binding, splat.Position, a, b, c, i);
            bindings[i] = binding;
        }

        return bindings;
    }

    /// <summary>
    /// Finds u, v, and h such that p = a + u(b − a) + v(c − a) + h·n̂, where n̂ is the unit normal of the triangle.
    /// </summary>
    /// <remarks>
    /// With n = ab × ac, u = ((ap × ac)·n) / |n|² and v = ((ab × ap)·n) / |n|². The error of this form grows with
    /// 1 / sin θ of the triangle, not with 1 / sin² θ as a solve of the 2 × 2 Gram system does. The calculation uses
    /// double precision, because u and v are not clamped and can be far outside the triangle.
    /// </remarks>
    private static void Project(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float h)
    {
        double abX = (double)b.X - a.X, abY = (double)b.Y - a.Y, abZ = (double)b.Z - a.Z;
        double acX = (double)c.X - a.X, acY = (double)c.Y - a.Y, acZ = (double)c.Z - a.Z;
        double apX = (double)p.X - a.X, apY = (double)p.Y - a.Y, apZ = (double)p.Z - a.Z;

        double nX = abY * acZ - abZ * acY;
        double nY = abZ * acX - abX * acZ;
        double nZ = abX * acY - abY * acX;
        double nLengthSquared = nX * nX + nY * nY + nZ * nZ;

        // ap × ac
        double uX = apY * acZ - apZ * acY;
        double uY = apZ * acX - apX * acZ;
        double uZ = apX * acY - apY * acX;

        // ab × ap
        double vX = abY * apZ - abZ * apY;
        double vY = abZ * apX - abX * apZ;
        double vZ = abX * apY - abY * apX;

        u = (float)((uX * nX + uY * nY + uZ * nZ) / nLengthSquared);
        v = (float)((vX * nX + vY * nY + vZ * nZ) / nLengthSquared);
        h = (float)((apX * nX + apY * nY + apZ * nZ) / Math.Sqrt(nLengthSquared));
    }

    /// <summary>
    /// Throws when the binding is not finite or does not reconstruct the splat center within
    /// <see cref="MaxReconstructErrorMeters"/>.
    /// </summary>
    /// <remarks>
    /// The test reconstructs the offset from corner a, not the world position. A float world position far from the
    /// origin has a rounding step larger than the tolerance (2 steps at 128 m are 1.5e-5 m), and that rounding is
    /// the same for every way to store the splat. The offset test still finds a wrong u, v, or h.
    /// </remarks>
    private static void Verify(SplatBinding binding, Vector3 position, Vector3 a, Vector3 b, Vector3 c, int splatIndex)
    {
        Quaternion rotation = binding.LocalRotation;
        if (!float.IsFinite(binding.U) || !float.IsFinite(binding.V) || !float.IsFinite(binding.H)
            || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
        {
            throw new InvalidOperationException(
                $"The binding of splat {splatIndex} to driver triangle {binding.Triangle} is not finite. This is a defect of the bake.");
        }

        Vector3 offset = TriangleFrame.Reconstruct(Vector3.Zero, b - a, c - a, binding.U, binding.V, binding.H);
        float error = Vector3.Distance(offset, position - a);
        if (!(error <= MaxReconstructErrorMeters))
        {
            throw new InvalidOperationException(
                $"The binding of splat {splatIndex} to driver triangle {binding.Triangle} reconstructs its center {error:E3} m "
                + $"from its rest position, which is more than {MaxReconstructErrorMeters:E0} m. This is a defect of the bake.");
        }
    }
}
