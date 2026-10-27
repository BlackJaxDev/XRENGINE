using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>
/// Holds the nested driver meshes of a bake, from level 0 (the coarsest) to the top level (the finest).
/// <see cref="DriverHierarchyBuilder"/> makes it.
/// </summary>
/// <remarks>
/// <para>The driver vertices are in one array in coarse-first order: the vertices of level 0, then the vertices that
/// level 1 adds, and so on. Level <c>k</c> uses exactly the driver vertices from 0 to
/// <c>CumulativeVertexCounts[k] − 1</c>, so the vertices of each level are a subset of the vertices of the next level.</para>
/// <para>Each driver vertex is one vertex of the <see cref="DeformedSurface"/>. Its trajectory is the trajectory of
/// that surface vertex.</para>
/// </remarks>
public sealed class DriverHierarchy
{
    /// <summary>Gets a hierarchy with no levels, for content that has no usable animated triangle.</summary>
    public static DriverHierarchy Empty { get; } = new([], [], [], []);

    /// <summary>Makes a hierarchy and checks that its arrays agree.</summary>
    /// <param name="cumulativeVertexCounts">The number of driver vertices that each level uses. The values must not decrease.</param>
    /// <param name="levelTriangleIndices">The triangle list of each level, as indices into the driver vertex array.</param>
    /// <param name="sourceVertexOfDriverVertex">The surface vertex of each driver vertex.</param>
    /// <param name="targetVertexCounts">The vertex count that the simplification of each level tried to reach.</param>
    /// <exception cref="ArgumentException">The arrays do not agree.</exception>
    public DriverHierarchy(int[] cumulativeVertexCounts, uint[][] levelTriangleIndices, int[] sourceVertexOfDriverVertex, int[] targetVertexCounts)
    {
        ArgumentNullException.ThrowIfNull(cumulativeVertexCounts);
        ArgumentNullException.ThrowIfNull(levelTriangleIndices);
        ArgumentNullException.ThrowIfNull(sourceVertexOfDriverVertex);
        ArgumentNullException.ThrowIfNull(targetVertexCounts);
        int levelCount = levelTriangleIndices.Length;
        if (cumulativeVertexCounts.Length != levelCount || targetVertexCounts.Length != levelCount)
            throw new ArgumentException("There must be one cumulative vertex count and one target vertex count for each level.", nameof(cumulativeVertexCounts));

        int previous = 0;
        for (int level = 0; level < levelCount; level++)
        {
            int count = cumulativeVertexCounts[level];
            if (count < previous)
                throw new ArgumentException($"The cumulative vertex count of level {level} is less than the count of the level below it.", nameof(cumulativeVertexCounts));

            string? problem = FindTriangleProblem(levelTriangleIndices[level], count, level);
            if (problem is not null)
                throw new ArgumentException(problem, nameof(levelTriangleIndices));

            previous = count;
        }

        if (previous != sourceVertexOfDriverVertex.Length)
            throw new ArgumentException("The cumulative vertex count of the top level must be the number of driver vertices.", nameof(sourceVertexOfDriverVertex));

        CumulativeVertexCounts = cumulativeVertexCounts;
        LevelTriangleIndices = levelTriangleIndices;
        SourceVertexOfDriverVertex = sourceVertexOfDriverVertex;
        TargetVertexCounts = targetVertexCounts;
        LevelCount = levelCount;
    }

    /// <summary>Gets the number of levels. The value is 0 when the content has no animated mesh, or when every animated triangle is degenerate or a duplicate.</summary>
    public int LevelCount { get; }

    /// <summary>
    /// Gets, for each level, the number of driver vertices that the level uses. Level <c>k</c> uses the driver
    /// vertices from 0 to <c>CumulativeVertexCounts[k] − 1</c>.
    /// </summary>
    public int[] CumulativeVertexCounts { get; }

    /// <summary>Gets the triangle list of each level. The indices refer to the coarse-first driver vertex array.</summary>
    public uint[][] LevelTriangleIndices { get; }

    /// <summary>Gets, for each driver vertex, the index of its vertex in the <see cref="DeformedSurface"/>.</summary>
    public int[] SourceVertexOfDriverVertex { get; }

    /// <summary>
    /// Gets the vertex count that the simplification of each level tried to reach. When the simplifier cannot reach
    /// it, the level keeps the closest result, and <see cref="CumulativeVertexCounts"/> is more than the target.
    /// </summary>
    public int[] TargetVertexCounts { get; }

    /// <summary>Gets the number of driver vertices in all levels.</summary>
    public int VertexCount
        => SourceVertexOfDriverVertex.Length;

    /// <summary>Writes the position of each driver vertex in the surface into <paramref name="destination"/>. It does not allocate.</summary>
    /// <param name="surface">The surface at one time. It must have the vertices of the surface that the hierarchy was made from.</param>
    /// <param name="destination">The positions, one for each driver vertex. It must hold <see cref="VertexCount"/> items.</param>
    public void GatherPositions(DeformedSurface surface, Span<Vector3> destination)
    {
        ArgumentNullException.ThrowIfNull(surface);
        int[] sources = SourceVertexOfDriverVertex;
        if (destination.Length < sources.Length)
            throw new ArgumentException("The destination is smaller than the number of driver vertices.", nameof(destination));

        float[] positions = surface.Positions;
        for (int driver = 0; driver < sources.Length; driver++)
        {
            int offset = sources[driver] * 3;
            destination[driver] = new Vector3(positions[offset], positions[offset + 1], positions[offset + 2]);
        }
    }

    /// <summary>Returns a description of the first problem in the triangle list of a level, or null when the list is correct.</summary>
    private static string? FindTriangleProblem(uint[]? indices, int vertexCount, int level)
    {
        if (indices is null)
            return $"Level {level} has no triangle list.";
        if (indices.Length % 3 != 0)
            return $"The triangle list of level {level} has a length that is not a multiple of 3.";

        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= (uint)vertexCount)
                return $"Level {level} has the index {indices[i]}, which is not less than its vertex count {vertexCount}.";
        }

        return null;
    }
}
