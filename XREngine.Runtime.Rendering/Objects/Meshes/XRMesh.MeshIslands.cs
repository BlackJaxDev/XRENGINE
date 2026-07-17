using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private const int SpatialPartitionBinCount = 16;
    private const float OptionalSpatialSplitCostRatio = 0.40f;

    /// <summary>
    /// Partitions a triangle mesh into spatially coherent, bounded-size meshes.
    /// This is intended for CPU draw/query culling: a query can only reject an
    /// entire draw, so material-wide meshes need tighter spatial draw units.
    /// </summary>
    public IReadOnlyList<XRMesh> PartitionTrianglesSpatially(int maxTrianglesPerPartition)
    {
        if (maxTrianglesPerPartition <= 0 ||
            Type != EPrimitiveType.Triangles ||
            _triangles is not { Count: > 1 } triangles ||
            Vertices is not { Length: > 0 })
        {
            return [this];
        }

        TriangleSpatialData[] spatialData = new TriangleSpatialData[triangles.Count];
        int[] root = new int[triangles.Count];
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            IndexTriangle triangle = triangles[triangleIndex];
            Vector3 point0 = GetTrianglePointPosition(triangle.Point0);
            Vector3 point1 = GetTrianglePointPosition(triangle.Point1);
            Vector3 point2 = GetTrianglePointPosition(triangle.Point2);
            spatialData[triangleIndex] = new TriangleSpatialData(
                (point0 + point1 + point2) / 3.0f,
                Vector3.Min(point0, Vector3.Min(point1, point2)),
                Vector3.Max(point0, Vector3.Max(point1, point2)));
            root[triangleIndex] = triangleIndex;
        }

        // Complete every hard size split first. Optional refinement cannot share this
        // work queue: an unresolved mandatory node represents more than one eventual
        // leaf, so counting it as one would let optional splits exceed their budget.
        List<int[]> mandatoryPending = [root];
        List<int[]> mandatoryPartitions = [];
        while (mandatoryPending.Count > 0)
        {
            int lastIndex = mandatoryPending.Count - 1;
            int[] indices = mandatoryPending[lastIndex];
            mandatoryPending.RemoveAt(lastIndex);

            if (indices.Length <= maxTrianglesPerPartition)
            {
                mandatoryPartitions.Add(indices);
                continue;
            }

            int minimumChildTriangles = GetMinimumMandatoryChildTriangleCount(
                indices.Length,
                maxTrianglesPerPartition);
            bool foundSplit = TrySplitByBinnedSurfaceArea(
                indices,
                spatialData,
                minimumChildTriangles,
                out int[] leftPartition,
                out int[] rightPartition,
                out _);

            if (!foundSplit)
                SplitAtMedian(indices, spatialData, out leftPartition, out rightPartition);

            mandatoryPending.Add(rightPartition);
            mandatoryPending.Add(leftPartition);
        }

        int mandatoryLeafCount = mandatoryPartitions.Count;
        int optionalLeafLimit = Math.Max(
            mandatoryLeafCount,
            mandatoryLeafCount <= 16
                ? Math.Max(4, mandatoryLeafCount * 4)
                : 64);
        int minimumOptionalChildTriangles = Math.Max(
            1,
            Math.Min(128, maxTrianglesPerPartition / SpatialPartitionBinCount));

        List<int[]> pending = new(mandatoryLeafCount);
        for (int partitionIndex = mandatoryLeafCount - 1; partitionIndex >= 0; partitionIndex--)
            pending.Add(mandatoryPartitions[partitionIndex]);

        List<int[]> partitions = new(mandatoryLeafCount);
        while (pending.Count > 0)
        {
            int lastIndex = pending.Count - 1;
            int[] indices = pending[lastIndex];
            pending.RemoveAt(lastIndex);

            bool optionalSplitAllowed =
                partitions.Count + pending.Count + 2 <= optionalLeafLimit &&
                indices.Length >= minimumOptionalChildTriangles * 2;
            if (!optionalSplitAllowed ||
                !TrySplitByBinnedSurfaceArea(
                    indices,
                    spatialData,
                    minimumOptionalChildTriangles,
                    out int[] leftPartition,
                    out int[] rightPartition,
                    out float splitCostRatio) ||
                splitCostRatio > OptionalSpatialSplitCostRatio)
            {
                partitions.Add(indices);
                continue;
            }

            pending.Add(rightPartition);
            pending.Add(leftPartition);
        }

        if (partitions.Count == 1 && partitions[0].Length == triangles.Count)
            return [this];

        List<XRMesh> meshes = new(partitions.Count);
        for (int partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++)
            meshes.Add(CreateTriangleSubsetMesh(partitions[partitionIndex], "Partition", partitionIndex));
        return meshes;
    }

    private static int GetMinimumMandatoryChildTriangleCount(
        int triangleCount,
        int maxTrianglesPerPartition)
    {
        // A node only slightly above the limit may peel off its exact overflow and
        // finish in one split. Larger nodes require a 25% child to prevent repeated
        // 1-vs-rest SAH splits and their quadratic preprocessing cost.
        if ((long)triangleCount <= (long)maxTrianglesPerPartition * 2L)
            return Math.Max(1, triangleCount - maxTrianglesPerPartition);

        return Math.Max(1, triangleCount / 4);
    }

    private static bool TrySplitByBinnedSurfaceArea(
        IReadOnlyList<int> indices,
        IReadOnlyList<TriangleSpatialData> spatialData,
        int minimumChildTriangles,
        out int[] leftPartition,
        out int[] rightPartition,
        out float splitCostRatio)
    {
        leftPartition = [];
        rightPartition = [];
        splitCostRatio = float.PositiveInfinity;

        Vector3 parentMin = new(float.PositiveInfinity);
        Vector3 parentMax = new(float.NegativeInfinity);
        Vector3 centroidMin = new(float.PositiveInfinity);
        Vector3 centroidMax = new(float.NegativeInfinity);
        for (int index = 0; index < indices.Count; index++)
        {
            TriangleSpatialData data = spatialData[indices[index]];
            parentMin = Vector3.Min(parentMin, data.Min);
            parentMax = Vector3.Max(parentMax, data.Max);
            centroidMin = Vector3.Min(centroidMin, data.Centroid);
            centroidMax = Vector3.Max(centroidMax, data.Centroid);
        }

        float parentCost = SurfaceArea(parentMin, parentMax) * indices.Count;
        if (!float.IsFinite(parentCost) || parentCost <= float.Epsilon)
            return false;

        int bestAxis = -1;
        int bestSplitBin = -1;
        float bestAxisMin = 0.0f;
        float bestAxisExtent = 0.0f;
        float bestCost = float.PositiveInfinity;

        Span<int> binCounts = stackalloc int[SpatialPartitionBinCount];
        Span<Vector3> binMins = stackalloc Vector3[SpatialPartitionBinCount];
        Span<Vector3> binMaxes = stackalloc Vector3[SpatialPartitionBinCount];
        Span<int> leftCounts = stackalloc int[SpatialPartitionBinCount];
        Span<Vector3> leftMins = stackalloc Vector3[SpatialPartitionBinCount];
        Span<Vector3> leftMaxes = stackalloc Vector3[SpatialPartitionBinCount];
        Span<int> rightCounts = stackalloc int[SpatialPartitionBinCount];
        Span<Vector3> rightMins = stackalloc Vector3[SpatialPartitionBinCount];
        Span<Vector3> rightMaxes = stackalloc Vector3[SpatialPartitionBinCount];

        for (int axis = 0; axis < 3; axis++)
        {
            float axisMin = GetAxis(centroidMin, axis);
            float axisExtent = GetAxis(centroidMax, axis) - axisMin;
            if (!float.IsFinite(axisExtent) || axisExtent <= 1.0e-6f)
                continue;

            binCounts.Clear();
            InitializeEmptyBounds(binMins, binMaxes);
            for (int index = 0; index < indices.Count; index++)
            {
                TriangleSpatialData data = spatialData[indices[index]];
                int bin = GetSpatialBin(GetAxis(data.Centroid, axis), axisMin, axisExtent);
                binCounts[bin]++;
                binMins[bin] = Vector3.Min(binMins[bin], data.Min);
                binMaxes[bin] = Vector3.Max(binMaxes[bin], data.Max);
            }

            int runningCount = 0;
            Vector3 runningMin = new(float.PositiveInfinity);
            Vector3 runningMax = new(float.NegativeInfinity);
            for (int bin = 0; bin < SpatialPartitionBinCount; bin++)
            {
                if (binCounts[bin] > 0)
                {
                    runningCount += binCounts[bin];
                    runningMin = Vector3.Min(runningMin, binMins[bin]);
                    runningMax = Vector3.Max(runningMax, binMaxes[bin]);
                }

                leftCounts[bin] = runningCount;
                leftMins[bin] = runningMin;
                leftMaxes[bin] = runningMax;
            }

            runningCount = 0;
            runningMin = new Vector3(float.PositiveInfinity);
            runningMax = new Vector3(float.NegativeInfinity);
            for (int bin = SpatialPartitionBinCount - 1; bin >= 0; bin--)
            {
                if (binCounts[bin] > 0)
                {
                    runningCount += binCounts[bin];
                    runningMin = Vector3.Min(runningMin, binMins[bin]);
                    runningMax = Vector3.Max(runningMax, binMaxes[bin]);
                }

                rightCounts[bin] = runningCount;
                rightMins[bin] = runningMin;
                rightMaxes[bin] = runningMax;
            }

            for (int splitBin = 0; splitBin < SpatialPartitionBinCount - 1; splitBin++)
            {
                int leftCount = leftCounts[splitBin];
                int rightCount = rightCounts[splitBin + 1];
                if (leftCount < minimumChildTriangles || rightCount < minimumChildTriangles)
                    continue;

                float cost =
                    SurfaceArea(leftMins[splitBin], leftMaxes[splitBin]) * leftCount +
                    SurfaceArea(rightMins[splitBin + 1], rightMaxes[splitBin + 1]) * rightCount;
                if (cost >= bestCost)
                    continue;

                bestCost = cost;
                bestAxis = axis;
                bestSplitBin = splitBin;
                bestAxisMin = axisMin;
                bestAxisExtent = axisExtent;
            }
        }

        if (bestAxis < 0)
            return false;

        int finalLeftCount = 0;
        for (int index = 0; index < indices.Count; index++)
        {
            TriangleSpatialData data = spatialData[indices[index]];
            int bin = GetSpatialBin(GetAxis(data.Centroid, bestAxis), bestAxisMin, bestAxisExtent);
            if (bin <= bestSplitBin)
                finalLeftCount++;
        }

        if (finalLeftCount <= 0 || finalLeftCount >= indices.Count)
            return false;

        leftPartition = new int[finalLeftCount];
        rightPartition = new int[indices.Count - finalLeftCount];
        int leftIndex = 0;
        int rightIndex = 0;
        for (int index = 0; index < indices.Count; index++)
        {
            int triangleIndex = indices[index];
            TriangleSpatialData data = spatialData[triangleIndex];
            int bin = GetSpatialBin(GetAxis(data.Centroid, bestAxis), bestAxisMin, bestAxisExtent);
            if (bin <= bestSplitBin)
                leftPartition[leftIndex++] = triangleIndex;
            else
                rightPartition[rightIndex++] = triangleIndex;
        }

        splitCostRatio = bestCost / parentCost;
        return true;
    }

    private static void SplitAtMedian(
        int[] indices,
        IReadOnlyList<TriangleSpatialData> spatialData,
        out int[] leftPartition,
        out int[] rightPartition)
    {
        int axis = FindLongestCentroidAxis(indices, spatialData);
        Array.Sort(indices, (left, right) =>
            GetAxis(spatialData[left].Centroid, axis).CompareTo(GetAxis(spatialData[right].Centroid, axis)));
        int leftCount = indices.Length / 2;
        leftPartition = new int[leftCount];
        rightPartition = new int[indices.Length - leftCount];
        Array.Copy(indices, 0, leftPartition, 0, leftPartition.Length);
        Array.Copy(indices, leftPartition.Length, rightPartition, 0, rightPartition.Length);
    }

    private static void InitializeEmptyBounds(Span<Vector3> mins, Span<Vector3> maxes)
    {
        for (int index = 0; index < mins.Length; index++)
        {
            mins[index] = new Vector3(float.PositiveInfinity);
            maxes[index] = new Vector3(float.NegativeInfinity);
        }
    }

    private static int GetSpatialBin(float value, float min, float extent)
    {
        float normalized = Math.Clamp((value - min) / extent, 0.0f, 1.0f);
        return Math.Min(SpatialPartitionBinCount - 1, (int)(normalized * SpatialPartitionBinCount));
    }

    private static float SurfaceArea(in Vector3 min, in Vector3 max)
    {
        Vector3 extent = Vector3.Max(max - min, Vector3.Zero);
        return 2.0f * (extent.X * extent.Y + extent.X * extent.Z + extent.Y * extent.Z);
    }

    private readonly record struct TriangleSpatialData(Vector3 Centroid, Vector3 Min, Vector3 Max);

    /// <summary>
    /// Separates disconnected triangle islands into standalone meshes.
    /// Connectivity is evaluated by exact vertex position so imported meshes with duplicated
    /// per-face vertices still resolve into coherent geometric islands.
    /// </summary>
    public IReadOnlyList<XRMesh> SeparateTriangleIslands()
    {
        if (Type != EPrimitiveType.Triangles || _triangles is not { Count: > 1 } triangles || Vertices is not { Length: > 0 })
            return [this];

        Dictionary<Vector3, List<int>> trianglesByPosition = BuildTrianglePositionMap(triangles);
        bool[] visited = new bool[triangles.Count];
        Queue<int> pending = new();
        List<int> islandTriangleIndices = new(triangles.Count);
        List<XRMesh> islands = [];

        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            if (visited[triangleIndex])
                continue;

            islandTriangleIndices.Clear();
            visited[triangleIndex] = true;
            pending.Enqueue(triangleIndex);

            while (pending.Count > 0)
            {
                int currentTriangleIndex = pending.Dequeue();
                islandTriangleIndices.Add(currentTriangleIndex);

                IndexTriangle triangle = triangles[currentTriangleIndex];
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point0), trianglesByPosition, visited, pending);
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point1), trianglesByPosition, visited, pending);
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point2), trianglesByPosition, visited, pending);
            }

            if (islandTriangleIndices.Count == triangles.Count)
                return [this];

            islands.Add(CreateTriangleSubsetMesh(islandTriangleIndices, "Island", islands.Count));
        }

        return islands.Count <= 1 ? [this] : islands;
    }

    private Dictionary<Vector3, List<int>> BuildTrianglePositionMap(IReadOnlyList<IndexTriangle> triangles)
    {
        Dictionary<Vector3, List<int>> trianglesByPosition = new(triangles.Count * 3);
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            IndexTriangle triangle = triangles[triangleIndex];
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point0), triangleIndex, trianglesByPosition);
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point1), triangleIndex, trianglesByPosition);
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point2), triangleIndex, trianglesByPosition);
        }

        return trianglesByPosition;
    }

    private Vector3 GetTrianglePointPosition(int vertexIndex)
        => vertexIndex >= 0 && vertexIndex < Vertices.Length
            ? Vertices[vertexIndex].Position
            : Vector3.Zero;

    private static void AddTrianglePosition(
        Vector3 position,
        int triangleIndex,
        Dictionary<Vector3, List<int>> trianglesByPosition)
    {
        if (!trianglesByPosition.TryGetValue(position, out List<int>? triangleIndices))
        {
            triangleIndices = [];
            trianglesByPosition.Add(position, triangleIndices);
        }

        if (triangleIndices.Count == 0 || triangleIndices[^1] != triangleIndex)
            triangleIndices.Add(triangleIndex);
    }

    private static void EnqueueAdjacentTriangles(
        Vector3 position,
        IReadOnlyDictionary<Vector3, List<int>> trianglesByPosition,
        bool[] visited,
        Queue<int> pending)
    {
        if (!trianglesByPosition.TryGetValue(position, out List<int>? adjacentTriangles))
            return;

        for (int index = 0; index < adjacentTriangles.Count; index++)
        {
            int adjacentTriangle = adjacentTriangles[index];
            if (visited[adjacentTriangle])
                continue;

            visited[adjacentTriangle] = true;
            pending.Enqueue(adjacentTriangle);
        }
    }

    private static int FindLongestCentroidAxis(
        IReadOnlyList<int> indices,
        IReadOnlyList<TriangleSpatialData> spatialData)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (int index = 0; index < indices.Count; index++)
        {
            Vector3 centroid = spatialData[indices[index]].Centroid;
            min = Vector3.Min(min, centroid);
            max = Vector3.Max(max, centroid);
        }

        Vector3 extent = max - min;
        return extent.X >= extent.Y && extent.X >= extent.Z
            ? 0
            : extent.Y >= extent.Z ? 1 : 2;
    }

    private static float GetAxis(in Vector3 value, int axis)
        => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    private XRMesh CreateTriangleSubsetMesh(IReadOnlyList<int> triangleIndices, string label, int subsetIndex)
    {
        List<VertexTriangle> trianglePrimitives = new(triangleIndices.Count);
        for (int index = 0; index < triangleIndices.Count; index++)
        {
            IndexTriangle triangle = _triangles![triangleIndices[index]];
            trianglePrimitives.Add(new VertexTriangle(
                CopyVertexForIsland(Vertices[triangle.Point0]),
                CopyVertexForIsland(Vertices[triangle.Point1]),
                CopyVertexForIsland(Vertices[triangle.Point2])));
        }

        XRMesh island = new(trianglePrimitives)
        {
            Name = string.IsNullOrWhiteSpace(Name) ? $"{label} {subsetIndex}" : $"{Name} {label} {subsetIndex}",
            AllowBVHGeneration = AllowBVHGeneration,
            MaxBlendshapeAccumulation = MaxBlendshapeAccumulation,
            SupportsBillboarding = SupportsBillboarding,
            BindRootMatrix = BindRootMatrix,
            SkinningShaderConvention = SkinningShaderConvention,
            BlendshapeNames = [.. BlendshapeNames],
        };

        ESkinningShaderConvention skinningConvention = SkinningShaderConvention;
        if (HasAnyVertexWeights(island.Vertices))
        {
            island.RebuildSkinningBuffersFromVertices();
            island.SkinningShaderConvention = skinningConvention;
        }

        if (HasBlendshapes)
            island.RebuildBlendshapeBuffersFromVertices();

        return island;
    }

    private static Vertex CopyVertexForIsland(Vertex vertex)
        => vertex.HardCopy();

    private static bool HasAnyVertexWeights(IReadOnlyList<Vertex> vertices)
    {
        for (int index = 0; index < vertices.Count; index++)
        {
            if (vertices[index].Weights is { Count: > 0 })
                return true;
        }

        return false;
    }
}
