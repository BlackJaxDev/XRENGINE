using System.Runtime.InteropServices;
namespace XREngine.Rendering.Meshlets;

// Cluster hierarchy building blocks: cluster partitioning and bounds of clusters and sphere sets.
internal static partial class NativeMeshOptimizer
{
    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_partitionClusters")]
    private static extern unsafe nuint MeshoptPartitionClusters(uint* destination, uint* clusterIndices, nuint totalIndexCount, uint* clusterIndexCounts, nuint clusterCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, nuint targetPartitionSize);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_computeSphereBounds")]
    private static extern unsafe MeshOptimizerBounds MeshoptComputeSphereBounds(float* positions, nuint count, nuint positionsStride, float* radii, nuint radiiStride);

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_computeClusterBounds")]
    private static extern unsafe MeshOptimizerBounds MeshoptComputeClusterBounds(uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    /// <summary>
    /// Puts clusters into groups of about <paramref name="targetPartitionSize"/> clusters, so that a
    /// group holds clusters that share vertices or lie close to each other. Writes the group id of
    /// cluster i to <paramref name="destination"/>[i] and returns the number of groups.
    /// <paramref name="clusterIndices"/> holds the vertex indices of every cluster in sequence, and
    /// <paramref name="clusterIndexCounts"/>[i] is the index count of cluster i. Positions are three
    /// floats per vertex (stride in bytes); empty positions group by shared vertices only.
    /// </summary>
    public static unsafe int PartitionClusters(Span<uint> destination, ReadOnlySpan<uint> clusterIndices, ReadOnlySpan<uint> clusterIndexCounts, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int targetPartitionSize)
    {
        int clusterCount = clusterIndexCounts.Length;
        if (destination.Length < clusterCount)
            throw new ArgumentException("The destination is smaller than the cluster count.", nameof(destination));
        if (targetPartitionSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetPartitionSize), "The target partition size must be positive.");

        ulong totalIndexCount = 0;
        for (int i = 0; i < clusterCount; i++)
            totalIndexCount += clusterIndexCounts[i];
        if (totalIndexCount != (ulong)clusterIndices.Length)
            throw new ArgumentException("The cluster index counts do not add up to the cluster index length.", nameof(clusterIndexCounts));
        if (clusterCount == 0)
            return 0;

        foreach (uint index in clusterIndices)
            if (index >= (uint)vertexCount)
                throw new ArgumentException("A cluster index is outside the vertex range.", nameof(clusterIndices));

        bool hasPositions = !positions.IsEmpty;
        if (hasPositions)
            ValidatePositions(positions, vertexCount, positionsStride);

        fixed (uint* destinationPtr = destination)
        fixed (uint* indicesPtr = clusterIndices)
        fixed (uint* countsPtr = clusterIndexCounts)
        fixed (float* positionsPtr = positions)
        {
            return (int)MeshoptPartitionClusters(
                destinationPtr,
                indicesPtr,
                (nuint)clusterIndices.Length,
                countsPtr,
                (nuint)clusterCount,
                hasPositions ? positionsPtr : null,
                (nuint)vertexCount,
                hasPositions ? (nuint)positionsStride : 0,
                (nuint)targetPartitionSize);
        }
    }

    /// <summary>
    /// Returns a sphere that contains a set of points, or a set of spheres when
    /// <paramref name="radii"/> is not empty (one radius for each element, stride in bytes). Only the
    /// center and radius of the result are set. Positions are three floats per element.
    /// </summary>
    public static unsafe MeshOptimizerBounds ComputeSphereBounds(ReadOnlySpan<float> positions, int count, int positionsStride, ReadOnlySpan<float> radii, int radiiStride)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "The sphere bounds need at least one element.");
        ValidatePositions(positions, count, positionsStride);
        bool hasRadii = !radii.IsEmpty;
        if (hasRadii && (radiiStride < sizeof(float) || (long)(count - 1) * radiiStride / sizeof(float) + 1 > radii.Length))
            throw new ArgumentException("The radii do not cover every element.", nameof(radii));

        fixed (float* positionsPtr = positions)
        fixed (float* radiiPtr = radii)
            return MeshoptComputeSphereBounds(positionsPtr, (nuint)count, (nuint)positionsStride, hasRadii ? radiiPtr : null, hasRadii ? (nuint)radiiStride : 0);
    }

    /// <summary>
    /// Returns the bounding sphere and normal cone of a cluster given as a triangle index list into
    /// the full vertex array. The cluster may use at most 256 unique vertices and 512 triangles.
    /// </summary>
    public static unsafe MeshOptimizerBounds ComputeClusterBounds(ReadOnlySpan<uint> indices, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0 || indices.Length / 3 > 512)
            throw new ArgumentException("A cluster needs between 1 and 512 whole triangles.", nameof(indices));
        ValidatePositions(positions, vertexCount, positionsStride);
        foreach (uint index in indices)
            if (index >= (uint)vertexCount)
                throw new ArgumentException("A cluster index is outside the vertex range.", nameof(indices));

        fixed (uint* indicesPtr = indices)
        fixed (float* positionsPtr = positions)
            return MeshoptComputeClusterBounds(indicesPtr, (nuint)indices.Length, positionsPtr, (nuint)vertexCount, (nuint)positionsStride);
    }
}
