using System.Numerics;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;

namespace XREngine.Rendering.Models.Gaussian.Baking.Lod;

/// <summary>Builds the splat LOD tree by repeated clustering and moment-matched merging.</summary>
public static class SplatLodTreeBuilder
{
    /// <summary>The number of splats in a full cluster.</summary>
    private const int ClusterSize = 8;

    /// <summary>A last cluster that is smaller than this value is joined with the cluster before it.</summary>
    private const int MinClusterSize = 4;

    /// <summary>The bytes between the positions in the point buffer.</summary>
    private const int PositionStride = 3 * sizeof(float);

    /// <summary>
    /// Builds the tree. The static and the animated splats make two separate forests.
    /// Each forest merges until its coarsest level has at most its share of the target. A forest of fewer than four splats is not merged.
    /// <para>
    /// The levels of the two forests are aligned at the leaves: the last level of the tree holds the leaves of both forests.
    /// The level index counts from the coarsest level of the deeper forest.
    /// A shallower forest repeats its coarsest level in the first levels, and each repeated node has one child (itself in the next level).
    /// This keeps the child counts of every level equal to the size of the next level.
    /// Each level lists the static nodes first, then the animated nodes.
    /// </para>
    /// </summary>
    public static SplatLodTree Build(BakedSplat[] leaves, int l0Target, IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(leaves);
        ArgumentNullException.ThrowIfNull(meshopt);

        List<BakedSplat> staticLeaves = [];
        List<BakedSplat> animatedLeaves = [];
        foreach (BakedSplat leaf in leaves)
            (leaf.IsStatic ? staticLeaves : animatedLeaves).Add(leaf);

        int total = leaves.Length;
        Forest staticForest = BuildForest(staticLeaves.ToArray(), ShareOfTarget(l0Target, staticLeaves.Count, total), meshopt);
        Forest animatedForest = BuildForest(animatedLeaves.ToArray(), ShareOfTarget(l0Target, animatedLeaves.Count, total), meshopt);

        int depth = Math.Max(Math.Max(staticForest.Levels.Length, animatedForest.Levels.Length), 1);
        BakedSplat[][] levels = new BakedSplat[depth][];
        int[][] childCounts = new int[depth - 1][];
        for (int k = 0; k < depth; k++)
        {
            levels[k] = Concat(staticForest.LevelAt(k, depth), animatedForest.LevelAt(k, depth));
            if (k < depth - 1)
                childCounts[k] = Concat(staticForest.ChildCountsAt(k, depth), animatedForest.ChildCountsAt(k, depth));
        }
        return new SplatLodTree(levels, childCounts);
    }

    private static int ShareOfTarget(int l0Target, int forestCount, int total)
    {
        if (total <= 0 || forestCount <= 0)
            return 1;
        double share = (double)l0Target * forestCount / total;
        return Math.Max(1, (int)Math.Round(share, MidpointRounding.AwayFromZero));
    }

    private static T[] Concat<T>(T[] a, T[] b)
    {
        T[] result = new T[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    private sealed class Forest(BakedSplat[][] levels, int[][] childCounts)
    {
        public BakedSplat[][] Levels { get; } = levels;
        public int[][] ChildCounts { get; } = childCounts;

        /// <summary>Gets the nodes at a tree level. The levels are aligned at the leaves, and the coarsest level repeats above its depth.</summary>
        public BakedSplat[] LevelAt(int index, int depth)
        {
            if (Levels.Length == 0)
                return [];
            return Levels[Math.Max(index - (depth - Levels.Length), 0)];
        }

        /// <summary>Gets the child counts at a tree level. A repeated node has one child.</summary>
        public int[] ChildCountsAt(int index, int depth)
        {
            if (Levels.Length == 0)
                return [];
            int local = index - (depth - Levels.Length);
            if (local >= 0)
                return ChildCounts[local];
            int[] ones = new int[Levels[0].Length];
            Array.Fill(ones, 1);
            return ones;
        }
    }

    private static Forest BuildForest(BakedSplat[] leaves, int target, IMeshOptimizerBackend meshopt)
    {
        if (leaves.Length == 0)
            return new Forest([], []);

        // Levels in creation order, finest first. parentOf[i][n] is the creation index of the parent of node n of level i.
        List<BakedSplat[]> created = [leaves];
        List<int[]> parentOf = [];
        BakedSplat[] current = leaves;
        BakedSplat[] scratch = new BakedSplat[ClusterSize + MinClusterSize];
        while (current.Length > target && current.Length >= MinClusterSize)
        {
            int[] clusterSizes = GroupLevel(current, meshopt, out uint[] order);
            BakedSplat[] parents = new BakedSplat[clusterSizes.Length];
            int[] parentIndices = new int[current.Length];
            int cursor = 0;
            for (int p = 0; p < clusterSizes.Length; p++)
            {
                int size = clusterSizes[p];
                for (int c = 0; c < size; c++)
                {
                    uint node = order[cursor + c];
                    scratch[c] = current[node];
                    parentIndices[node] = p;
                }
                parents[p] = GaussianMomentMerge.Merge(scratch.AsSpan(0, size));
                cursor += size;
            }
            created.Add(parents);
            parentOf.Add(parentIndices);
            current = parents;
        }

        int depth = created.Count;
        BakedSplat[][] levels = new BakedSplat[depth][];
        int[][] counts = new int[depth - 1][];

        // The coarsest level keeps its creation order. Each finer level follows the final order of its parents.
        int coarsest = depth - 1;
        int[] finalOrder = new int[created[coarsest].Length];
        for (int i = 0; i < finalOrder.Length; i++)
            finalOrder[i] = i;
        levels[0] = created[coarsest];

        for (int creation = coarsest - 1; creation >= 0; creation--)
        {
            int[] parentIndex = parentOf[creation];
            int parentCount = created[creation + 1].Length;
            int[] childTotals = new int[parentCount];
            foreach (int p in parentIndex)
                childTotals[p]++;

            int[] start = new int[parentCount + 1];
            for (int p = 0; p < parentCount; p++)
                start[p + 1] = start[p] + childTotals[p];
            int[] fill = new int[parentCount];
            int[] bucket = new int[parentIndex.Length];
            for (int n = 0; n < parentIndex.Length; n++)
            {
                int p = parentIndex[n];
                bucket[start[p] + fill[p]++] = n;
            }

            int[] newOrder = new int[parentIndex.Length];
            int[] nodeCounts = new int[finalOrder.Length];
            int write = 0;
            for (int slot = 0; slot < finalOrder.Length; slot++)
            {
                int p = finalOrder[slot];
                nodeCounts[slot] = childTotals[p];
                for (int j = start[p]; j < start[p + 1]; j++)
                    newOrder[write++] = bucket[j];
            }

            int levelIndex = coarsest - creation;
            counts[levelIndex - 1] = nodeCounts;
            BakedSplat[] ordered = new BakedSplat[newOrder.Length];
            for (int i = 0; i < newOrder.Length; i++)
                ordered[i] = created[creation][newOrder[i]];
            levels[levelIndex] = ordered;
            finalOrder = newOrder;
        }
        return new Forest(levels, counts);
    }

    /// <summary>Groups the nodes of a level into clusters of four to eight. Returns the cluster sizes and the node order.</summary>
    private static int[] GroupLevel(BakedSplat[] level, IMeshOptimizerBackend meshopt, out uint[] order)
    {
        int n = level.Length;
        float[] positions = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            Vector3 p = level[i].Position;
            positions[i * 3] = p.X;
            positions[i * 3 + 1] = p.Y;
            positions[i * 3 + 2] = p.Z;
        }
        order = new uint[n];
        meshopt.SpatialClusterPoints(order, positions, n, PositionStride, ClusterSize);

        int full = n / ClusterSize;
        int rest = n % ClusterSize;
        List<int> sizes = new(full + 1);
        if (full == 0)
        {
            sizes.Add(rest);
        }
        else if (rest == 0 || rest >= MinClusterSize)
        {
            for (int i = 0; i < full; i++)
                sizes.Add(ClusterSize);
            if (rest != 0)
                sizes.Add(rest);
        }
        else
        {
            for (int i = 0; i < full - 1; i++)
                sizes.Add(ClusterSize);
            int joined = ClusterSize + rest;
            sizes.Add((joined + 1) / 2);
            sizes.Add(joined / 2);
        }
        return sizes.ToArray();
    }
}
