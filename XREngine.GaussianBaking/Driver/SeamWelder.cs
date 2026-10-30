using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>
/// Welds the vertices that a mesh splits at its UV, normal, or material seams, so that the driver simplification can
/// collapse edges across the seams. The weld keeps no trajectories: it checks each frame once, as the bake samples it.
/// </summary>
/// <remarks>
/// A weld remap gives, for each surface vertex, its canonical vertex. A canonical vertex maps to itself. All other
/// vertices map to a canonical vertex that had the same rest position and has stayed near it in every checked frame.
/// </remarks>
public static class SeamWelder
{
    private const int PositionStride = 3 * sizeof(float);

    /// <summary>
    /// Makes a weld remap that maps each vertex to the first vertex with the same rest position.
    /// </summary>
    /// <param name="rest">The surface at the rest pose.</param>
    /// <param name="meshopt">The mesh optimizer that finds the vertices with the same position.</param>
    /// <returns>The canonical vertex of each surface vertex.</returns>
    public static uint[] CreateRemap(DeformedSurface rest, IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(meshopt);

        uint[] remap = new uint[rest.VertexCount];
        meshopt.GeneratePositionRemap(remap, rest.Positions, rest.VertexCount, PositionStride);
        return remap;
    }

    /// <summary>
    /// Breaks the weld of each vertex that is more than <paramref name="toleranceMeters"/> from its canonical vertex
    /// in <paramref name="frame"/>: the vertex becomes its own canonical vertex. Call it once for each sampled frame.
    /// </summary>
    /// <remarks>
    /// A broken weld stays broken. When the canonical vertex moves away from two copies that stay together, the two
    /// copies are not welded to each other. This costs some simplification, but the result never welds vertices
    /// that move apart.
    /// </remarks>
    /// <param name="remap">The weld remap from <see cref="CreateRemap"/>. The method changes it.</param>
    /// <param name="frame">The surface at one sampled time. It must have the same vertices as the rest surface.</param>
    /// <param name="toleranceMeters">The largest distance between a vertex and its canonical vertex that keeps the weld. Use one grid step.</param>
    public static void BreakDivergent(uint[] remap, DeformedSurface frame, float toleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(remap);
        ArgumentNullException.ThrowIfNull(frame);
        if (remap.Length != frame.VertexCount)
            throw new ArgumentException("The remap must have one item for each vertex of the frame.", nameof(remap));
        if (!float.IsFinite(toleranceMeters) || toleranceMeters < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(toleranceMeters), toleranceMeters, "The tolerance must be a finite value that is not negative.");

        float[] positions = frame.Positions;
        float toleranceSquared = toleranceMeters * toleranceMeters;
        for (int vertex = 0; vertex < remap.Length; vertex++)
        {
            uint canonical = remap[vertex];
            if (canonical == (uint)vertex)
                continue;

            int a = vertex * 3;
            int b = (int)canonical * 3;
            float dx = positions[a] - positions[b];
            float dy = positions[a + 1] - positions[b + 1];
            float dz = positions[a + 2] - positions[b + 2];
            // A position that is not a number also breaks the weld.
            if (!(dx * dx + dy * dy + dz * dz <= toleranceSquared))
                remap[vertex] = (uint)vertex;
        }
    }
}
