using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Lod;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SplatLodTreeTests
{
    private IMeshOptimizerBackend _meshopt = null!;

    [OneTimeSetUp]
    public void InstallMeshProcessing()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;
    }

    [Test]
    public void Build_ChildCountsBetween4And8()
    {
        SplatLodTree tree = SplatLodTreeBuilder.Build(CreateLeaves(1_000, 0.5f), 20, _meshopt);

        tree.LevelCount.ShouldBeGreaterThan(1);
        tree.ChildCounts.Length.ShouldBe(tree.LevelCount - 1);
        for (int level = 0; level < tree.LevelCount - 1; level++)
        {
            tree.ChildCounts[level].Length.ShouldBe(tree.Levels[level].Length);
            tree.ChildCounts[level].Sum().ShouldBe(tree.Levels[level + 1].Length);
            foreach (int count in tree.ChildCounts[level])
            {
                count.ShouldBeGreaterThanOrEqualTo(4);
                count.ShouldBeLessThanOrEqualTo(8);
            }
        }
    }

    [Test]
    public void Build_StopsAtOrBelowL0Target()
    {
        SplatLodTree tree = SplatLodTreeBuilder.Build(CreateLeaves(1_000, 0.5f), 50, _meshopt);

        tree.Levels[0].Length.ShouldBeLessThanOrEqualTo(50);
        tree.Levels[^1].Length.ShouldBe(1_000);
    }

    [Test]
    public void Build_StaticNodesFirstInEveryLevel()
    {
        SplatLodTree tree = SplatLodTreeBuilder.Build(CreateLeaves(600, 0.4f), 30, _meshopt);

        foreach (BakedSplat[] level in tree.Levels)
        {
            bool seenAnimated = false;
            foreach (BakedSplat splat in level)
            {
                if (!splat.IsStatic)
                    seenAnimated = true;
                else
                    seenAnimated.ShouldBeFalse();
            }
        }
    }

    [Test]
    public void Build_ChildrenOfConsecutiveParentsAreContiguousInNextLevel()
    {
        SplatLodTree tree = SplatLodTreeBuilder.Build(CreateLeaves(500, 0.5f), 10, _meshopt);

        // Every leaf has the same weight, so a parent position is the mean of its child block.
        for (int level = 0; level < tree.LevelCount - 1; level++)
        {
            int start = 0;
            for (int parent = 0; parent < tree.Levels[level].Length; parent++)
            {
                int count = tree.ChildCounts[level][parent];
                Vector3 mean = Vector3.Zero;
                float weight = 0.0f;
                for (int c = start; c < start + count; c++)
                {
                    BakedSplat child = tree.Levels[level + 1][c];
                    float w = child.Opacity * child.Scale.X * child.Scale.Y * child.Scale.Z;
                    mean += w * child.Position;
                    weight += w;
                }
                AssertNear(mean / weight, tree.Levels[level][parent].Position, 1e-2f);
                start += count;
            }
        }
    }

    [Test]
    public void Build_IsDeterministic()
    {
        BakedSplat[] leaves = CreateLeaves(700, 0.5f);

        SplatLodTree a = SplatLodTreeBuilder.Build(leaves, 25, _meshopt);
        SplatLodTree b = SplatLodTreeBuilder.Build(leaves, 25, _meshopt);

        a.LevelCount.ShouldBe(b.LevelCount);
        for (int level = 0; level < a.LevelCount; level++)
        {
            a.Levels[level].Length.ShouldBe(b.Levels[level].Length);
            for (int i = 0; i < a.Levels[level].Length; i++)
            {
                a.Levels[level][i].Position.ShouldBe(b.Levels[level][i].Position);
                a.Levels[level][i].Scale.ShouldBe(b.Levels[level][i].Scale);
                a.Levels[level][i].Rotation.ShouldBe(b.Levels[level][i].Rotation);
            }
            if (level < a.LevelCount - 1)
                a.ChildCounts[level].ShouldBe(b.ChildCounts[level]);
        }
    }

    [Test]
    public void Build_FewerThanFourSplats_ReturnsSingleLevel()
    {
        SplatLodTree tree = SplatLodTreeBuilder.Build(CreateLeaves(3, 1.0f), 1, _meshopt);

        tree.LevelCount.ShouldBe(1);
        tree.Levels[0].Length.ShouldBe(3);
        tree.ChildCounts.Length.ShouldBe(0);
    }

    [Test]
    public void Build_ForestsOfDifferentDepth_AlignAtLeaves()
    {
        // 6 static splats merge once to 1. 600 animated splats need several passes.
        List<BakedSplat> leaves = [];
        leaves.AddRange(CreateLeaves(6, 1.0f));
        leaves.AddRange(CreateLeaves(600, 0.0f));

        SplatLodTree tree = SplatLodTreeBuilder.Build(leaves.ToArray(), 8, _meshopt);

        tree.Levels[^1].Length.ShouldBe(606);
        for (int level = 0; level < tree.LevelCount - 1; level++)
        {
            tree.ChildCounts[level].Length.ShouldBe(tree.Levels[level].Length);
            tree.ChildCounts[level].Sum().ShouldBe(tree.Levels[level + 1].Length);
        }
    }

    [Test]
    public void Merge_TwoIdenticalSplats_KeepsMeanColorAndShape()
    {
        BakedSplat splat = Splat(new Vector3(1, 2, 3), Quaternion.Identity, new Vector3(0.1f, 0.2f, 0.3f), 0.5f, true);
        splat.LinearColor = new Vector3(0.2f, 0.4f, 0.6f);

        BakedSplat merged = GaussianMomentMerge.Merge([splat, splat]);

        AssertNear(merged.Position, splat.Position, 1e-5f);
        AssertNear(merged.LinearColor, splat.LinearColor, 1e-5f);
        // The eigen decomposition sorts the axes by size, so compare the sorted sigmas.
        merged.Scale.X.ShouldBe(0.3f, 1e-4f);
        merged.Scale.Y.ShouldBe(0.2f, 1e-4f);
        merged.Scale.Z.ShouldBe(0.1f, 1e-4f);
        merged.SourceTriangle.ShouldBe(-1);
        merged.IsStatic.ShouldBeTrue();
    }

    [Test]
    public void Merge_TwoUnitSplatsAtPlusMinusOneOnX_ParentSigmaXIsSqrt2()
    {
        BakedSplat a = Splat(new Vector3(-1, 0, 0), Quaternion.Identity, Vector3.One, 1.0f, true);
        BakedSplat b = Splat(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One, 1.0f, false);

        BakedSplat merged = GaussianMomentMerge.Merge([a, b]);

        AssertNear(merged.Position, Vector3.Zero, 1e-5f);
        merged.Scale.X.ShouldBe(MathF.Sqrt(2.0f), 1e-4f);
        merged.Scale.Y.ShouldBe(1.0f, 1e-4f);
        merged.Scale.Z.ShouldBe(1.0f, 1e-4f);
        merged.IsStatic.ShouldBeFalse();
        Vector3 axis = Vector3.Transform(Vector3.UnitX, merged.Rotation);
        MathF.Abs(axis.X).ShouldBe(1.0f, 1e-4f);
    }

    [Test]
    public void Eigen_RecoversRotatedDiagonal()
    {
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f));
        Vector3 diagonal = new(9.0f, 4.0f, 1.0f);
        Vector3 x = Vector3.Transform(Vector3.UnitX, rotation);
        Vector3 y = Vector3.Transform(Vector3.UnitY, rotation);
        Vector3 z = Vector3.Transform(Vector3.UnitZ, rotation);
        float xx = diagonal.X * x.X * x.X + diagonal.Y * y.X * y.X + diagonal.Z * z.X * z.X;
        float xy = diagonal.X * x.X * x.Y + diagonal.Y * y.X * y.Y + diagonal.Z * z.X * z.Y;
        float xz = diagonal.X * x.X * x.Z + diagonal.Y * y.X * y.Z + diagonal.Z * z.X * z.Z;
        float yy = diagonal.X * x.Y * x.Y + diagonal.Y * y.Y * y.Y + diagonal.Z * z.Y * z.Y;
        float yz = diagonal.X * x.Y * x.Z + diagonal.Y * y.Y * y.Z + diagonal.Z * z.Y * z.Z;
        float zz = diagonal.X * x.Z * x.Z + diagonal.Y * y.Z * y.Z + diagonal.Z * z.Z * z.Z;

        (Vector3 values, Quaternion recovered) = SymmetricEigen3.Decompose(xx, xy, xz, yy, yz, zz);

        AssertNear(values, diagonal, 1e-4f);
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitX, recovered), x)).ShouldBe(1.0f, 1e-4f);
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitY, recovered), y)).ShouldBe(1.0f, 1e-4f);
        MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitZ, recovered), z)).ShouldBe(1.0f, 1e-4f);
    }

    private static void AssertNear(Vector3 actual, Vector3 expected, float tolerance)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }

    private static BakedSplat Splat(Vector3 position, Quaternion rotation, Vector3 scale, float opacity, bool isStatic)
        => new()
        {
            Position = position,
            Rotation = rotation,
            Scale = scale,
            LinearColor = new Vector3(0.5f, 0.5f, 0.5f),
            Opacity = opacity,
            Normal = Vector3.UnitZ,
            SourceTriangle = 0,
            IsStatic = isStatic,
        };

    /// <summary>Makes splats on a deterministic pseudo-random scatter. The fraction is the share that is static.</summary>
    private static BakedSplat[] CreateLeaves(int count, float staticFraction)
    {
        Random random = new(1234);
        BakedSplat[] leaves = new BakedSplat[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 position = new(random.NextSingle() * 10.0f, random.NextSingle() * 10.0f, random.NextSingle() * 10.0f);
            bool isStatic = random.NextSingle() < staticFraction;
            leaves[i] = Splat(position, Quaternion.Identity, new Vector3(0.1f, 0.1f, 0.1f), 0.8f, isStatic);
        }
        return leaves;
    }
}
