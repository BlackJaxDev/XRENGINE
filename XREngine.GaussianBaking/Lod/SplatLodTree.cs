using XREngine.Rendering.Models.Gaussian.Baking.Sampling;

namespace XREngine.Rendering.Models.Gaussian.Baking.Lod;

/// <summary>The coarse-to-fine levels of a splat set. The last level holds the sampled splats.</summary>
public sealed class SplatLodTree
{
    /// <summary>Makes a tree from its levels and child counts.</summary>
    public SplatLodTree(BakedSplat[][] levels, int[][] childCounts)
    {
        Levels = levels;
        ChildCounts = childCounts;
    }

    /// <summary>The number of levels.</summary>
    public int LevelCount => Levels.Length;

    /// <summary>The splats of each level. Level 0 is the coarsest. In each level the static splats are first.</summary>
    public BakedSplat[][] Levels { get; }

    /// <summary>
    /// For each level except the last: the number of children of each node.
    /// The children of consecutive parents are contiguous in the next level, so the child starts are the prefix sums.
    /// </summary>
    public int[][] ChildCounts { get; }
}
