using XREngine.Rendering.Models.Gaussian.Baking.Surface;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>Finds the meshes of a surface that move during the bake.</summary>
public static class MotionClassifier
{
    /// <summary>
    /// Marks each mesh that has a vertex which moves more than <paramref name="thresholdMeters"/>.
    /// </summary>
    /// <param name="rest">The surface at the rest pose.</param>
    /// <param name="maxDisplacementPerVertex">The largest distance that each vertex moves from its rest position over the bake.</param>
    /// <param name="thresholdMeters">The distance that a vertex must move more than.</param>
    /// <returns>One value for each mesh. The value is true when the mesh is animated.</returns>
    public static bool[] FindAnimatedMeshes(DeformedSurface rest, ReadOnlySpan<float> maxDisplacementPerVertex, float thresholdMeters)
    {
        ArgumentNullException.ThrowIfNull(rest);
        if (maxDisplacementPerVertex.Length != rest.VertexCount)
            throw new ArgumentException("There must be one displacement for each vertex.", nameof(maxDisplacementPerVertex));

        bool[] animated = new bool[rest.MeshCount];
        for (int mesh = 0; mesh < rest.MeshCount; mesh++)
        {
            int end = rest.MeshVertexStart[mesh + 1];
            for (int vertex = rest.MeshVertexStart[mesh]; vertex < end; vertex++)
            {
                if (maxDisplacementPerVertex[vertex] > thresholdMeters)
                {
                    animated[mesh] = true;
                    break;
                }
            }
        }

        return animated;
    }
}
