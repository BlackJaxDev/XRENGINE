using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;

namespace XREngine.Rendering.Models.Gaussian.Baking.Driver;

/// <summary>Makes the nested driver meshes of a bake from the animated meshes of the rest surface.</summary>
public static class DriverHierarchyBuilder
{
    /// <summary>The smallest vertex target of a level below the top level.</summary>
    public const int MinLevelVertexCount = 64;

    /// <summary>The first target index count of a level is this value × its vertex target.</summary>
    private const int IndicesPerTargetVertex = 6;

    /// <summary>The simplification stops when the target index count is less than one triangle.</summary>
    private const int MinTargetIndexCount = 3;

    /// <summary>The largest number of simplifier calls that scale the target index count before the reduction changes to halving.</summary>
    private const int MaxRetargetPasses = 16;

    /// <summary>A scaled target index count is multiplied by this value, so that the next result is a small amount under the vertex target.</summary>
    private const double RetargetMargin = 0.98;

    /// <summary>A scaled target index count is at least this much less than the previous target.</summary>
    private const int MinRetargetStep = 3;

    /// <summary>The absolute error limit of <see cref="IMeshOptimizerBackend.Simplify"/>. It is so large that only the index target stops it.</summary>
    private const float SimplifyTargetError = 1e30f;

    /// <summary>The relative error limit of <see cref="IMeshOptimizerBackend.SimplifySloppy"/>. It is the largest value in its range.</summary>
    private const float SloppyTargetError = 1.0f;

    /// <summary>
    /// The simplification options. Prune is not set, because a pruned small part would bind its splats to distant
    /// triangles.
    /// </summary>
    private const MeshOptimizerSimplifyOptions SimplifyOptions = MeshOptimizerSimplifyOptions.ErrorAbsolute | MeshOptimizerSimplifyOptions.Regularize;

    private const int PositionStride = 3 * sizeof(float);

    /// <summary>
    /// Makes the driver hierarchy. There is one driver level for each splat level, and level <c>k</c> drives splat level <c>k</c>.
    /// </summary>
    /// <remarks>
    /// <para>The steps are:</para>
    /// <list type="number">
    /// <item>Select the triangles of the animated meshes. When there are none, the hierarchy has no levels.</item>
    /// <item>Weld the triangle indices with <paramref name="weldRemap"/>.</item>
    /// <item>Remove the degenerate and duplicate triangles with <see cref="IMeshOptimizerBackend.FilterIndexBuffer"/>.</item>
    /// <item>Simplify the top level to at most <see cref="GaussianBakeBudget.MaxDriverVertices"/> used vertices.</item>
    /// <item>Simplify each lower level <c>k</c> from level <c>k + 1</c> to <c>max(64, round(V_top × |L_k| / |L_top|))</c>
    /// vertices, where <c>V_top</c> is the vertex count of the top level and <c>|L_k|</c> is the splat count of level <c>k</c>.</item>
    /// <item>Put the vertices in coarse-first order. The new vertices of each level are in spatial order.</item>
    /// </list>
    /// <para>The simplification never moves a vertex (it does not use <see cref="IMeshOptimizerBackend.SimplifyWithUpdate"/>),
    /// because the vertex trajectories are keyed by the surface vertex. Each result uses a subset of the vertices of its
    /// input, so the levels are nested.</para>
    /// </remarks>
    /// <param name="rest">The surface at the rest pose.</param>
    /// <param name="animatedMeshes">One value for each mesh of the surface. The value is true when the mesh is animated.</param>
    /// <param name="weldRemap">The canonical vertex of each surface vertex (see <see cref="SeamWelder"/>).</param>
    /// <param name="splatLevelCounts">The number of splats in each splat level, from the coarsest to the finest.</param>
    /// <param name="budget">The bake budget.</param>
    /// <param name="meshopt">The mesh optimizer.</param>
    public static DriverHierarchy Build(
        DeformedSurface rest,
        bool[] animatedMeshes,
        ReadOnlySpan<uint> weldRemap,
        IReadOnlyList<int> splatLevelCounts,
        GaussianBakeBudget budget,
        IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(animatedMeshes);
        ArgumentNullException.ThrowIfNull(splatLevelCounts);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(meshopt);
        if (animatedMeshes.Length != rest.MeshCount)
            throw new ArgumentException("There must be one value for each mesh of the surface.", nameof(animatedMeshes));
        if (weldRemap.Length != rest.VertexCount)
            throw new ArgumentException("The weld remap must have one item for each vertex of the surface.", nameof(weldRemap));
        if (splatLevelCounts.Count == 0)
            throw new ArgumentException("There must be at least one splat level.", nameof(splatLevelCounts));
        for (int level = 0; level < splatLevelCounts.Count; level++)
        {
            if (splatLevelCounts[level] <= 0)
                throw new ArgumentException($"Splat level {level} must have at least one splat.", nameof(splatLevelCounts));
        }

        if (budget.MaxDriverVertices < 3)
            throw new ArgumentException("The budget must allow at least 3 driver vertices.", nameof(budget));

        uint[] welded = SelectAndWeld(rest, animatedMeshes, weldRemap);
        if (welded.Length == 0)
            return DriverHierarchy.Empty;

        uint[] clean = new uint[welded.Length];
        int cleanCount = meshopt.FilterIndexBuffer(clean, welded, rest.Positions, rest.VertexCount, PositionStride, PositionStride);
        if (cleanCount == 0)
            return DriverHierarchy.Empty;

        int[] scratch = new int[rest.VertexCount];
        Array.Fill(scratch, -1);
        int levelCount = splatLevelCounts.Count;
        int top = levelCount - 1;
        uint[][] levels = new uint[levelCount][];
        int[] targets = new int[levelCount];
        targets[top] = budget.MaxDriverVertices;
        levels[top] = Reduce(clean.AsSpan(0, cleanCount).ToArray(), rest.Positions, targets[top], meshopt, scratch);

        int topVertexCount = CountUsedVertices(levels[top], scratch);
        for (int level = top - 1; level >= 0; level--)
        {
            double share = (double)topVertexCount * splatLevelCounts[level] / splatLevelCounts[top];
            targets[level] = Math.Max(MinLevelVertexCount, (int)Math.Min(Math.Round(share, MidpointRounding.AwayFromZero), int.MaxValue));
            levels[level] = Reduce(levels[level + 1], rest.Positions, targets[level], meshopt, scratch);
        }

        return Order(levels, targets, rest.Positions, scratch, meshopt);
    }

    /// <summary>Returns the triangles of the animated meshes, with each index replaced by its canonical vertex.</summary>
    private static uint[] SelectAndWeld(DeformedSurface rest, bool[] animatedMeshes, ReadOnlySpan<uint> weldRemap)
    {
        int[] triangleMesh = rest.TriangleMesh;
        uint[] indices = rest.Indices;
        int selectedCount = 0;
        for (int triangle = 0; triangle < rest.TriangleCount; triangle++)
        {
            if (animatedMeshes[triangleMesh[triangle]])
                selectedCount++;
        }

        uint[] welded = new uint[selectedCount * 3];
        int next = 0;
        for (int triangle = 0; triangle < rest.TriangleCount; triangle++)
        {
            if (!animatedMeshes[triangleMesh[triangle]])
                continue;

            for (int corner = 0; corner < 3; corner++)
            {
                uint canonical = weldRemap[(int)indices[triangle * 3 + corner]];
                if (canonical >= (uint)rest.VertexCount)
                    throw new ArgumentException($"The weld remap maps a vertex to {canonical}, which is not a vertex of the surface.", nameof(weldRemap));

                welded[next++] = canonical;
            }
        }

        return welded;
    }

    /// <summary>
    /// Simplifies the triangles until they use at most <paramref name="targetVertexCount"/> vertices, and as close to
    /// that count as the simplifier allows.
    /// </summary>
    /// <remarks>
    /// <para>The target index count starts at 6 × the vertex target. Each result is simplified again from the previous
    /// result, so the first result at or under the vertex target is the closest one. After a result that is still above
    /// the vertex target, the target index count becomes <c>floor(target × vertex target / used vertices × 0.98)</c>,
    /// and it always goes down by at least 3. When <see cref="IMeshOptimizerBackend.Simplify"/> makes no progress, the
    /// reduction continues with <see cref="IMeshOptimizerBackend.SimplifySloppy"/> at the same target.</para>
    /// <para>After <see cref="MaxRetargetPasses"/> simplifier calls without a result at or under the vertex target, the
    /// reduction halves the target until it gets there (see <see cref="ReduceByHalving"/>).</para>
    /// </remarks>
    /// <returns>The simplified triangles, as surface vertex indices. Its vertices are a subset of the vertices of <paramref name="triangles"/>.</returns>
    private static uint[] Reduce(uint[] triangles, float[] positions, int targetVertexCount, IMeshOptimizerBackend meshopt, int[] scratch)
    {
        int usedVertexCount = CountUsedVertices(triangles, scratch);
        if (usedVertexCount <= targetVertexCount)
            return triangles;

        int startIndexCount = (int)Math.Min((long)IndicesPerTargetVertex * targetVertexCount, int.MaxValue);
        int targetIndexCount = startIndexCount;
        uint[] current = triangles;
        bool sloppy = false;
        for (int pass = 0; pass < MaxRetargetPasses; pass++)
        {
            // A target that is not less than the current index count cannot remove a triangle, so scale it without a call.
            while (targetIndexCount >= current.Length && targetIndexCount >= MinTargetIndexCount)
                targetIndexCount = Retarget(targetIndexCount, targetVertexCount, usedVertexCount);

            if (targetIndexCount < MinTargetIndexCount)
                break;

            uint[] reduced = SimplifyOnce(current, positions, targetIndexCount, sloppy, meshopt, scratch);
            if (reduced.Length == 0)
                return current;

            if (reduced.Length >= current.Length)
            {
                // No progress. The topology stops the simplifier, so change to the sloppy simplifier at the same target.
                // When the sloppy simplifier makes no progress, try a smaller target.
                if (!sloppy)
                    sloppy = true;
                else
                    targetIndexCount = Retarget(targetIndexCount, targetVertexCount, usedVertexCount);
                continue;
            }

            current = reduced;
            usedVertexCount = CountUsedVertices(current, scratch);
            if (usedVertexCount <= targetVertexCount)
                return current;

            targetIndexCount = Retarget(targetIndexCount, targetVertexCount, usedVertexCount);
        }

        return ReduceByHalving(current, positions, targetVertexCount, targetIndexCount, startIndexCount, sloppy, meshopt, scratch);
    }

    /// <summary>
    /// Returns <c>floor(targetIndexCount × targetVertexCount / usedVertexCount × 0.98)</c>, but at least 3 less than
    /// <paramref name="targetIndexCount"/>. Call it only when <paramref name="usedVertexCount"/> is more than <paramref name="targetVertexCount"/>.
    /// </summary>
    private static int Retarget(int targetIndexCount, int targetVertexCount, int usedVertexCount)
    {
        double scaled = Math.Floor((double)targetIndexCount * targetVertexCount / usedVertexCount * RetargetMargin);
        return (int)Math.Min(scaled, (double)targetIndexCount - MinRetargetStep);
    }

    /// <summary>
    /// Continues a reduction that did not reach the vertex target within <see cref="MaxRetargetPasses"/> calls.
    /// </summary>
    /// <remarks>
    /// The target index count is halved after each result that is still above the vertex target. When
    /// <see cref="IMeshOptimizerBackend.Simplify"/> makes no more progress, or its target goes below one triangle first,
    /// the reduction continues with <see cref="IMeshOptimizerBackend.SimplifySloppy"/>. When the sloppy target goes below
    /// one triangle, or the sloppy simplifier would remove every triangle, the method returns the closest result. Each
    /// pass either returns, halves the target, or changes to the sloppy simplifier one time, so the loop always stops.
    /// </remarks>
    private static uint[] ReduceByHalving(uint[] current, float[] positions, int targetVertexCount, int targetIndexCount, int startIndexCount, bool sloppy, IMeshOptimizerBackend meshopt, int[] scratch)
    {
        while (true)
        {
            // A target that is not less than the current index count cannot remove a triangle.
            while (targetIndexCount >= current.Length && targetIndexCount >= MinTargetIndexCount)
                targetIndexCount /= 2;

            if (targetIndexCount < MinTargetIndexCount)
            {
                if (sloppy)
                    return current;

                // The simplifier made a small amount of progress at each target but did not reach the vertex target.
                // Give the sloppy simplifier one pass from the first target. This happens only once.
                sloppy = true;
                targetIndexCount = startIndexCount;
                continue;
            }

            uint[] reduced = SimplifyOnce(current, positions, targetIndexCount, sloppy, meshopt, scratch);
            if (reduced.Length == 0)
                return current;

            if (reduced.Length >= current.Length)
            {
                // No progress. The topology stops the simplifier, so change to the sloppy simplifier at the same target.
                // When the sloppy simplifier makes no progress, try a smaller target.
                if (!sloppy)
                    sloppy = true;
                else
                    targetIndexCount /= 2;
                continue;
            }

            current = reduced;
            if (CountUsedVertices(current, scratch) <= targetVertexCount)
                return current;

            targetIndexCount /= 2;
        }
    }

    /// <summary>
    /// Runs the simplifier once on a vertex buffer that holds only the vertices of <paramref name="triangles"/>.
    /// </summary>
    /// <remarks>
    /// The sloppy simplifier can use any vertex of its vertex buffer as the representative of a grid cell. With only the
    /// used vertices in the buffer, the result is always a subset of the vertices of the input.
    /// </remarks>
    private static uint[] SimplifyOnce(uint[] triangles, float[] positions, int targetIndexCount, bool sloppy, IMeshOptimizerBackend meshopt, int[] scratch)
    {
        int[] sourceOfLocal = CollectUsedVertices(triangles, scratch);
        float[] localPositions = new float[sourceOfLocal.Length * 3];
        for (int local = 0; local < sourceOfLocal.Length; local++)
        {
            int source = sourceOfLocal[local];
            scratch[source] = local;
            localPositions[local * 3] = positions[source * 3];
            localPositions[local * 3 + 1] = positions[source * 3 + 1];
            localPositions[local * 3 + 2] = positions[source * 3 + 2];
        }

        uint[] localIndices = new uint[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
            localIndices[i] = (uint)scratch[triangles[i]];

        foreach (int source in sourceOfLocal)
            scratch[source] = -1;

        int count = sloppy
            ? meshopt.SimplifySloppy(localIndices, localPositions, sourceOfLocal.Length, targetIndexCount, SloppyTargetError, null, out _)
            : meshopt.Simplify(localIndices, localPositions, sourceOfLocal.Length, targetIndexCount, SimplifyTargetError, SimplifyOptions, out _);

        uint[] result = new uint[count];
        for (int i = 0; i < count; i++)
            result[i] = (uint)sourceOfLocal[localIndices[i]];

        return result;
    }

    /// <summary>
    /// Gives the driver vertices their coarse-first order and converts the levels to driver vertex indices.
    /// </summary>
    private static DriverHierarchy Order(uint[][] levels, int[] targets, float[] positions, int[] scratch, IMeshOptimizerBackend meshopt)
    {
        // The scratch array holds the driver index of each surface vertex that has one, and -1 for the others.
        List<int> sourceOfDriver = [];
        int[] cumulative = new int[levels.Length];
        for (int level = 0; level < levels.Length; level++)
        {
            List<int> added = [];
            foreach (uint index in levels[level])
            {
                if (scratch[index] != -1)
                    continue;

                scratch[index] = -2;
                added.Add((int)index);
            }

            added.Sort();
            int start = sourceOfDriver.Count;
            if (added.Count != 0)
            {
                float[] addedPositions = new float[added.Count * 3];
                for (int i = 0; i < added.Count; i++)
                {
                    int source = added[i];
                    addedPositions[i * 3] = positions[source * 3];
                    addedPositions[i * 3 + 1] = positions[source * 3 + 1];
                    addedPositions[i * 3 + 2] = positions[source * 3 + 2];
                }

                // The remap gives the new place of each added vertex.
                uint[] spatialOrder = new uint[added.Count];
                meshopt.SpatialSortRemap(spatialOrder, addedPositions, added.Count, PositionStride);
                int[] ordered = new int[added.Count];
                for (int i = 0; i < added.Count; i++)
                {
                    int place = (int)spatialOrder[i];
                    ordered[place] = added[i];
                    scratch[added[i]] = start + place;
                }

                sourceOfDriver.AddRange(ordered);
            }

            cumulative[level] = sourceOfDriver.Count;
        }

        uint[][] levelTriangles = new uint[levels.Length][];
        for (int level = 0; level < levels.Length; level++)
        {
            uint[] source = levels[level];
            uint[] driver = new uint[source.Length];
            for (int i = 0; i < source.Length; i++)
                driver[i] = (uint)scratch[source[i]];
            levelTriangles[level] = driver;
        }

        foreach (int source in sourceOfDriver)
            scratch[source] = -1;

        return new DriverHierarchy(cumulative, levelTriangles, [.. sourceOfDriver], targets);
    }

    /// <summary>Returns the distinct vertices of the triangles in ascending order. The scratch array is -1 for every vertex before and after the call.</summary>
    private static int[] CollectUsedVertices(uint[] triangles, int[] scratch)
    {
        List<int> used = [];
        foreach (uint index in triangles)
        {
            if (scratch[index] != -1)
                continue;

            scratch[index] = 0;
            used.Add((int)index);
        }

        foreach (int vertex in used)
            scratch[vertex] = -1;

        used.Sort();
        return [.. used];
    }

    private static int CountUsedVertices(uint[] triangles, int[] scratch)
        => CollectUsedVertices(triangles, scratch).Length;
}
