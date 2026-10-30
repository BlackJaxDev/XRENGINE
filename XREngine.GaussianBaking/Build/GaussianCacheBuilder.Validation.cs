using System.Numerics;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Lod;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

// Checks that the parts of the input agree before the builder writes anything.
public static partial class GaussianCacheBuilder
{
    /// <summary>Checks the input and returns the number of static splats of each level.</summary>
    /// <exception cref="ArgumentException">The parts of the input do not agree.</exception>
    private static int[] Validate(GaussianCacheBuildInput input)
    {
        SplatLodTree tree = input.Tree ?? throw new ArgumentException("The input has no splat tree.", nameof(input));
        DriverHierarchy drivers = input.Drivers ?? throw new ArgumentException("The input has no driver hierarchy.", nameof(input));
        DriverFrameStore frames = input.Frames ?? throw new ArgumentException("The input has no frame store.", nameof(input));
        SplatBinding[][] bindings = input.BindingsPerLevel ?? throw new ArgumentException("The input has no bindings.", nameof(input));
        Vector3[] rest = input.DriverRestPositions ?? throw new ArgumentException("The input has no driver rest positions.", nameof(input));
        if (input.Motion is null)
            throw new ArgumentException("The input has no motion choice.", nameof(input));

        int[] staticCounts = ValidateTree(tree);
        if (drivers.LevelCount == 0)
        {
            for (int level = 0; level < tree.LevelCount; level++)
            {
                if (staticCounts[level] != tree.Levels[level].Length)
                    throw new ArgumentException($"Splat level {level} has animated splats, but there is no driver level.", nameof(input));
            }

            if (bindings.Any(levelBindings => levelBindings is { Length: > 0 }))
                throw new ArgumentException("There are bindings, but there is no driver level.", nameof(input));
        }
        else
        {
            if (drivers.LevelCount != tree.LevelCount)
                throw new ArgumentException($"There are {drivers.LevelCount} driver levels and {tree.LevelCount} splat levels. The counts must be equal.", nameof(input));
            if (bindings.Length != tree.LevelCount)
                throw new ArgumentException("There must be one binding array for each splat level.", nameof(input));
            if (rest.Length != drivers.VertexCount)
                throw new ArgumentException("There must be one rest position for each driver vertex.", nameof(input));
            for (int level = 0; level < tree.LevelCount; level++)
                ValidateBindings(bindings[level], tree.Levels[level].Length - staticCounts[level], drivers.LevelTriangleIndices[level].Length / 3, level);
        }

        if (frames.DriverLevelCount != drivers.LevelCount || frames.VertexCount != drivers.VertexCount)
            throw new ArgumentException("The frame store does not have the driver levels of the hierarchy.", nameof(input));
        for (int level = 0; level < drivers.LevelCount; level++)
        {
            int first = level == 0 ? 0 : drivers.CumulativeVertexCounts[level - 1];
            if (frames.GetLevelFirstVertex(level) != first || frames.GetLevelVertexCount(level) != drivers.CumulativeVertexCounts[level] - first)
                throw new ArgumentException($"The frame store has other vertices than the hierarchy in driver level {level}.", nameof(input));
        }

        if (frames.FrameCount != input.Motion.FrameCount)
            throw new ArgumentException($"The frame store has {frames.FrameCount} frames and the motion has {input.Motion.FrameCount}.", nameof(input));
        if (frames.LoopMode != input.LoopMode)
            throw new ArgumentException("The loop mode of the frame store is not the loop mode of the input.", nameof(input));
        if (input.GridBits is < 1 or > GridQuantizer.MaxGridBits)
            throw new ArgumentException($"The grid bits must be from 1 to {GridQuantizer.MaxGridBits}.", nameof(input));
        if (!float.IsFinite(input.ColorScale) || input.ColorScale < 1.0f)
            throw new ArgumentException("The color scale must be finite and at least 1.", nameof(input));
        if (!IsFinite(input.Bounds.Min) || !IsFinite(input.Bounds.Max) || Vector3.Max(input.Bounds.Min, input.Bounds.Max) != input.Bounds.Max)
            throw new ArgumentException("The bounds must be finite, and the minimum must not be more than the maximum.", nameof(input));

        return staticCounts;
    }

    /// <summary>Checks the levels and the child counts of the tree, and returns the static count of each level.</summary>
    private static int[] ValidateTree(SplatLodTree tree)
    {
        if (tree.LevelCount == 0 || tree.LevelCount > byte.MaxValue)
            throw new ArgumentException($"The tree must have from 1 to {byte.MaxValue} levels.", nameof(tree));
        if (tree.ChildCounts.Length != tree.LevelCount - 1)
            throw new ArgumentException("The tree must have child counts for each level except the last.", nameof(tree));

        int[] staticCounts = new int[tree.LevelCount];
        for (int level = 0; level < tree.LevelCount; level++)
        {
            BakedSplat[] splats = tree.Levels[level];
            if (splats.Length == 0)
                throw new ArgumentException($"Splat level {level} is empty.", nameof(tree));

            int staticCount = 0;
            while (staticCount < splats.Length && splats[staticCount].IsStatic)
                staticCount++;
            for (int node = 0; node < splats.Length; node++)
            {
                if (node >= staticCount && splats[node].IsStatic)
                    throw new ArgumentException($"Splat level {level} has a static splat after an animated splat.", nameof(tree));
                if (!IsFinite(splats[node]))
                    throw new ArgumentException($"Splat {node} of level {level} has a value that is not finite.", nameof(tree));
            }

            staticCounts[level] = staticCount;
            if (level == tree.LevelCount - 1)
                continue;

            int[] childCounts = tree.ChildCounts[level];
            if (childCounts.Length != splats.Length)
                throw new ArgumentException($"Level {level} must have one child count for each splat.", nameof(tree));
            long childTotal = 0;
            foreach (int count in childCounts)
            {
                if (count is < 1 or > byte.MaxValue)
                    throw new ArgumentException($"A child count of level {level} is not from 1 to {byte.MaxValue}.", nameof(tree));
                childTotal += count;
            }

            if (childTotal != tree.Levels[level + 1].Length)
                throw new ArgumentException($"The child counts of level {level} do not add up to the size of level {level + 1}.", nameof(tree));
        }

        return staticCounts;
    }

    private static void ValidateBindings(SplatBinding[]? bindings, int animatedCount, int triangleCount, int level)
    {
        if (bindings is null || bindings.Length != animatedCount)
            throw new ArgumentException($"Splat level {level} has {animatedCount} animated splats, so it must have {animatedCount} bindings.", nameof(bindings));

        for (int i = 0; i < bindings.Length; i++)
        {
            SplatBinding binding = bindings[i];
            if (binding.Triangle >= (uint)triangleCount)
                throw new ArgumentException($"Binding {i} of level {level} names triangle {binding.Triangle}, but driver level {level} has {triangleCount} triangles.", nameof(bindings));
            Quaternion rotation = binding.LocalRotation;
            if (!float.IsFinite(binding.U) || !float.IsFinite(binding.V) || !float.IsFinite(binding.H)
                || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
                throw new ArgumentException($"Binding {i} of level {level} has a value that is not finite.", nameof(bindings));
        }
    }

    private static bool IsFinite(in BakedSplat splat)
        => IsFinite(splat.Position) && IsFinite(splat.Scale) && IsFinite(splat.LinearColor) && float.IsFinite(splat.Opacity)
            && float.IsFinite(splat.Rotation.X) && float.IsFinite(splat.Rotation.Y) && float.IsFinite(splat.Rotation.Z) && float.IsFinite(splat.Rotation.W);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
