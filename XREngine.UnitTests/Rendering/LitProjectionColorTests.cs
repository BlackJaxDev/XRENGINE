using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking.Color;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class LitProjectionColorTests
{
    private const int Width = 32;
    private const int Height = 18;
    private const float FovDegrees = 40.0f;

    /// <summary>A camera on +Z that looks at the origin along -Z.</summary>
    private static readonly BakeCameraPose FrontCamera = new(new Vector3(0, 0, 5), Quaternion.Identity, FovDegrees);

    [Test]
    public void Apply_SingleView_TakesPixelColor()
    {
        BakedSplat[] leaves = [Leaf(Vector3.Zero, Vector3.UnitZ)];
        DeformedSurface rest = FarTriangleSurface();
        float[][] images = [ConstantImage(0.25f, 0.5f, 0.75f)];

        float scale = LitProjectionColorMath.Apply(leaves, rest, [FrontCamera], images, Width, Height);

        scale.ShouldBe(1.0f);
        leaves[0].LinearColor.X.ShouldBe(0.25f, 1e-5f);
        leaves[0].LinearColor.Y.ShouldBe(0.5f, 1e-5f);
        leaves[0].LinearColor.Z.ShouldBe(0.75f, 1e-5f);
    }

    [Test]
    public void Apply_OccludedLeaf_IgnoresView()
    {
        BakedSplat[] leaves = [Leaf(Vector3.Zero, Vector3.UnitZ)];
        DeformedSurface rest = Evaluate(
            [new(-1, -1, 2), new(1, -1, 2), new(1, 1, 2), new(-1, 1, 2)],
            [0, 1, 2, 0, 2, 3]);

        LitProjectionColorAccumulator accumulator = new(leaves, rest, Width, Height);
        accumulator.Accumulate([FrontCamera], [ConstantImage(0.5f, 0.5f, 0.5f)]);
        float scale = accumulator.Finish();

        accumulator.SeenLeafCount.ShouldBe(0);
        scale.ShouldBe(1.0f);
        leaves[0].LinearColor.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void Apply_BackFacingLeaf_IgnoresView()
    {
        BakedSplat[] leaves = [Leaf(Vector3.Zero, -Vector3.UnitZ)];
        DeformedSurface rest = FarTriangleSurface();

        LitProjectionColorAccumulator accumulator = new(leaves, rest, Width, Height);
        accumulator.Accumulate([FrontCamera], [ConstantImage(0.5f, 0.5f, 0.5f)]);
        accumulator.Finish();

        accumulator.SeenLeafCount.ShouldBe(0);
        leaves[0].LinearColor.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void Apply_UnseenLeaf_UsesNearestSeen()
    {
        // Leaves 1 and 4 are in the frame. The others are far outside it.
        BakedSplat[] leaves =
        [
            Leaf(new Vector3(100, 0, 0), Vector3.UnitZ),
            Leaf(new Vector3(-0.5f, 0, 0), Vector3.UnitZ),
            Leaf(new Vector3(100, 0, 0), Vector3.UnitZ),
            Leaf(new Vector3(100, 0, 0), Vector3.UnitZ),
            Leaf(new Vector3(0.5f, 0, 0), Vector3.UnitZ),
        ];
        DeformedSurface rest = FarTriangleSurface();

        LitProjectionColorMath.Apply(leaves, rest, [FrontCamera], [GradientImage()], Width, Height);

        leaves[1].LinearColor.Y.ShouldBeLessThan(0.5f);
        leaves[4].LinearColor.Y.ShouldBeGreaterThan(0.5f);
        leaves[0].LinearColor.ShouldBe(leaves[1].LinearColor);
        leaves[2].LinearColor.ShouldBe(leaves[1].LinearColor);
        leaves[3].LinearColor.ShouldBe(leaves[4].LinearColor);
    }

    [Test]
    public void Apply_TenPercentHighlights_ScaleEqualsMaxAndKeepsColorsAtOrBelow1()
    {
        // 10 percent of the leaves are highlights, so p99 equals the maximum here. Colors fit in 1 only in this case.
        // 90 leaves see a value of 1 on the left. 10 leaves see a value of 4 on the right.
        BakedSplat[] leaves = new BakedSplat[100];
        for (int i = 0; i < 90; i++)
            leaves[i] = Leaf(new Vector3(-0.8f + 0.001f * i, 0, 0), Vector3.UnitZ);
        for (int i = 90; i < 100; i++)
            leaves[i] = Leaf(new Vector3(0.8f + 0.001f * i, 0, 0), Vector3.UnitZ);

        float[] image = new float[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float value = x < Width / 2 ? 1.0f : 4.0f;
                int offset = (y * Width + x) * 4;
                image[offset] = value;
                image[offset + 1] = value;
                image[offset + 2] = value;
                image[offset + 3] = 1.0f;
            }
        }

        float scale = LitProjectionColorMath.Apply(leaves, FarTriangleSurface(), [FrontCamera], [image], Width, Height);

        scale.ShouldBe(4.0f, 1e-4f);
        foreach (BakedSplat leaf in leaves)
        {
            leaf.LinearColor.X.ShouldBeLessThanOrEqualTo(1.0f + 1e-5f);
            leaf.LinearColor.Y.ShouldBeLessThanOrEqualTo(1.0f + 1e-5f);
            leaf.LinearColor.Z.ShouldBeLessThanOrEqualTo(1.0f + 1e-5f);
        }

        leaves[0].LinearColor.X.ShouldBe(0.25f, 1e-4f);
        leaves[99].LinearColor.X.ShouldBe(1.0f, 1e-4f);
    }

    [Test]
    public void Apply_SingleOutlier_ScaleIsP99BelowMax()
    {
        // 199 leaves see a luminance of 2 on the left. One outlier sees a luminance of 8 on the right.
        const int count = 200;
        BakedSplat[] leaves = new BakedSplat[count];
        double[] luminances = new double[count];
        for (int i = 0; i < count - 1; i++)
        {
            leaves[i] = Leaf(new Vector3(-0.8f + 0.001f * i, 0, 0), Vector3.UnitZ);
            luminances[i] = 2.0;
        }

        leaves[count - 1] = Leaf(new Vector3(0.8f, 0, 0), Vector3.UnitZ);
        luminances[count - 1] = 8.0;

        float[] image = new float[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float value = x < Width / 2 ? 2.0f : 8.0f;
                int offset = (y * Width + x) * 4;
                image[offset] = value;
                image[offset + 1] = value;
                image[offset + 2] = value;
                image[offset + 3] = 1.0f;
            }
        }

        // Nearest-rank 99th percentile, computed here without the code under test.
        Array.Sort(luminances);
        double expectedScale = luminances[(int)Math.Ceiling(0.99 * count) - 1];

        float scale = LitProjectionColorMath.Apply(leaves, FarTriangleSurface(), [FrontCamera], [image], Width, Height);

        scale.ShouldBe((float)expectedScale, 1e-4f);
        scale.ShouldBeLessThan((float)luminances[count - 1]);
        // The outlier stays above 1 after scaling. The 8-bit encode clamps values above 1 later.
        leaves[count - 1].LinearColor.X.ShouldBeGreaterThan(1.0f);
        leaves[0].LinearColor.X.ShouldBe(1.0f, 1e-4f);
    }

    [Test]
    public void Apply_PlannerView_ProjectsLookAtPointToImageCenterAndUpToTopHalf()
    {
        AABB bounds = AABB.FromCenterSize(new Vector3(1, 2, -3), new Vector3(2, 2, 2));
        BakeCameraPose pose = BakeViewPlanner.PlanOrbitViews(bounds, 8)[2];
        Vector3 toCamera = Vector3.Normalize(pose.Position - bounds.Center);
        Vector3 up = Vector3.Transform(Vector3.UnitY, pose.Rotation);
        Vector3 right = Vector3.Transform(Vector3.UnitX, pose.Rotation);

        BakedSplat[] leaves =
        [
            Leaf(bounds.Center, toCamera),
            Leaf(bounds.Center + up * 0.3f, toCamera),
            Leaf(bounds.Center + right * 0.3f, toCamera),
        ];
        DeformedSurface rest = Evaluate([new(50, 50, 50), new(51, 50, 50), new(50, 51, 50)], [0, 1, 2]);

        LitProjectionColorMath.Apply(leaves, rest, [pose], [GradientImage()], Width, Height);

        // The gradient stores (x + 0.5) / width in green and (y + 0.5) / height in blue.
        leaves[0].LinearColor.Y.ShouldBe(0.5f, 1e-4f);
        leaves[0].LinearColor.Z.ShouldBe(0.5f, 1e-4f);
        leaves[1].LinearColor.Z.ShouldBeLessThan(0.45f);
        leaves[1].LinearColor.Y.ShouldBe(0.5f, 1e-4f);
        leaves[2].LinearColor.Y.ShouldBeGreaterThan(0.52f);
    }

    [Test]
    public async Task BakeAsync_CapturesInBatchesOfEight()
    {
        BakedSplat[] leaves = [Leaf(Vector3.Zero, Vector3.UnitZ)];
        List<int> batchSizes = [];
        SplatColorBakeContext context = new()
        {
            Leaves = leaves,
            Rest = FarTriangleSurface(),
            Views = Enumerable.Repeat(FrontCamera, 20).ToArray(),
            Width = Width,
            Height = Height,
            Capture = (views, _) =>
            {
                batchSizes.Add(views.Count);
                return Task.FromResult(views.Select(_ => ConstantImage(0.5f, 0.5f, 0.5f)).ToArray());
            },
        };

        LitProjectionColorBaker baker = new();
        float scale = await baker.BakeAsync(context, CancellationToken.None);

        baker.Mode.ShouldBe("lit-projection");
        batchSizes.ShouldBe([8, 8, 4]);
        scale.ShouldBe(1.0f);
        leaves[0].LinearColor.X.ShouldBe(0.5f, 1e-5f);
    }

    [Test]
    public void TriangleBvh_Raycast_MatchesBruteForce()
    {
        Random random = new(1234);
        const int triangleCount = 300;
        Vector3[] vertices = new Vector3[triangleCount * 3];
        ushort[] indices = new ushort[triangleCount * 3];
        for (int t = 0; t < triangleCount; t++)
        {
            Vector3 center = RandomPoint(random, 4.0f);
            for (int k = 0; k < 3; k++)
            {
                vertices[3 * t + k] = center + RandomPoint(random, 0.5f);
                indices[3 * t + k] = (ushort)(3 * t + k);
            }
        }

        DeformedSurface surface = Evaluate(vertices, indices);
        TriangleBvh bvh = new(surface);
        int hits = 0;

        for (int i = 0; i < 1000; i++)
        {
            Vector3 origin = RandomPoint(random, 6.0f);
            Vector3 direction = Vector3.Normalize(RandomPoint(random, 1.0f) - origin * 0.1f);
            float maxDistance = 2.0f + 10.0f * (float)random.NextDouble();

            bool expectedHit = BruteForce(surface, origin, direction, maxDistance, out float expectedDistance);
            bool actualHit = bvh.Raycast(origin, direction, maxDistance, out float actualDistance);

            actualHit.ShouldBe(expectedHit);
            if (expectedHit)
            {
                actualDistance.ShouldBe(expectedDistance, 1e-4f);
                hits++;
            }
        }

        hits.ShouldBeGreaterThan(50);
    }

    private static bool BruteForce(DeformedSurface surface, Vector3 origin, Vector3 direction, float maxDistance, out float nearest)
    {
        nearest = maxDistance;
        bool found = false;
        for (int t = 0; t < surface.TriangleCount; t++)
        {
            Vector3 a = Vertex(surface, surface.Indices[3 * t]);
            Vector3 b = Vertex(surface, surface.Indices[3 * t + 1]);
            Vector3 c = Vertex(surface, surface.Indices[3 * t + 2]);
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 p = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (MathF.Abs(determinant) < 1e-12f)
                continue;

            float inverse = 1.0f / determinant;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * inverse;
            Vector3 q = Vector3.Cross(s, edge1);
            float v = Vector3.Dot(direction, q) * inverse;
            if (u < 0.0f || u > 1.0f || v < 0.0f || u + v > 1.0f)
                continue;

            float distance = Vector3.Dot(edge2, q) * inverse;
            if (distance > 0.0f && distance <= nearest)
            {
                nearest = distance;
                found = true;
            }
        }

        return found;
    }

    private static Vector3 Vertex(DeformedSurface surface, uint index)
        => new(surface.Positions[3 * index], surface.Positions[3 * index + 1], surface.Positions[3 * index + 2]);

    private static Vector3 RandomPoint(Random random, float extent)
        => new(
            (float)(random.NextDouble() * 2.0 - 1.0) * extent,
            (float)(random.NextDouble() * 2.0 - 1.0) * extent,
            (float)(random.NextDouble() * 2.0 - 1.0) * extent);

    private static BakedSplat Leaf(Vector3 position, Vector3 normal)
        => new()
        {
            Position = position,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(0.01f),
            Normal = normal,
            Opacity = 1.0f,
        };

    private static float[] ConstantImage(float r, float g, float b)
    {
        float[] image = new float[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            image[4 * i] = r;
            image[4 * i + 1] = g;
            image[4 * i + 2] = b;
            image[4 * i + 3] = 1.0f;
        }

        return image;
    }

    private static float[] GradientImage()
    {
        float[] image = new float[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                image[offset] = 0.5f;
                image[offset + 1] = (x + 0.5f) / Width;
                image[offset + 2] = (y + 0.5f) / Height;
                image[offset + 3] = 1.0f;
            }
        }

        return image;
    }

    private static DeformedSurface FarTriangleSurface()
        => Evaluate([new(3, 3, -3), new(4, 3, -3), new(3, 4, -3)], [0, 1, 2]);

    private static DeformedSurface Evaluate(Vector3[] vertices, ushort[] triangles)
    {
        SceneNode root = new("Root", new Transform());
        XRMesh mesh = new(vertices.Select(position => new Vertex(position)), [.. triangles]);
        SceneNode node = new(root, "Mesh", new Transform());
        node.AddComponent<ModelComponent>().ShouldNotBeNull().Model = new Model(new SubMesh(mesh, new XRMaterial()));
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        return DeformedSurfaceEvaluator.Evaluate(root);
    }
}
