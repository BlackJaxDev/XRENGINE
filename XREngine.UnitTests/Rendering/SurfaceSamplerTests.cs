using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class SurfaceSamplerTests
{
    private const string SampleHash = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private IMeshOptimizerBackend _meshopt = null!;

    [OneTimeSetUp]
    public void InstallMeshProcessing()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;
    }

    [Test]
    public void Sample_IsDeterministicForSameSeed()
    {
        DeformedSurface surface = EvaluateTriangles(
            ([new(0, 0, 0), new(2, 0, 0), new(0, 1, 0)], [0, 1, 2]),
            ([new(0, 0, 1), new(2, 0, 1), new(0, 3, 1)], [0, 1, 2]));
        ulong seed = BakeRandom.SeedFrom(SampleHash, GaussianBakeKind.Avatar);

        BakedSplat[] first = SurfaceSampler.Sample(surface, 500, new BakeRandom(seed), _meshopt);
        BakedSplat[] second = SurfaceSampler.Sample(surface, 500, new BakeRandom(seed), _meshopt);

        first.Length.ShouldBe(500);
        for (int i = 0; i < first.Length; i++)
        {
            second[i].Position.ShouldBe(first[i].Position);
            second[i].Rotation.ShouldBe(first[i].Rotation);
            second[i].SourceTriangle.ShouldBe(first[i].SourceTriangle);
        }
    }

    [Test]
    public void Sample_DistributesByArea()
    {
        DeformedSurface surface = EvaluateTriangles(
            ([new(0, 0, 0), new(2, 0, 0), new(0, 1, 0)], [0, 1, 2]),
            ([new(0, 0, 1), new(2, 0, 1), new(0, 3, 1)], [0, 1, 2]));
        surface.TriangleCount.ShouldBe(2);

        BakedSplat[] splats = SurfaceSampler.Sample(surface, 100_000, new BakeRandom(12345UL), _meshopt);

        int onSmall = splats.Count(splat => splat.SourceTriangle == 0);
        splats.Count(splat => splat.SourceTriangle == 1).ShouldBe(splats.Length - onSmall);
        (onSmall / (double)splats.Length).ShouldBe(0.25, 0.02);
    }

    [Test]
    public void Sample_PlacesSplatsInsideTriangleWithTriangleFrame()
    {
        DeformedSurface surface = EvaluateTriangles(([new(0, 0, 2), new(2, 0, 2), new(0, 1, 2)], [0, 1, 2]));

        BakedSplat[] splats = SurfaceSampler.Sample(surface, 200, new BakeRandom(7UL), _meshopt);

        foreach (BakedSplat splat in splats)
        {
            splat.Position.Z.ShouldBe(2.0f, 1e-5f);
            splat.Position.X.ShouldBeGreaterThanOrEqualTo(-1e-5f);
            splat.Position.Y.ShouldBeGreaterThanOrEqualTo(-1e-5f);
            (splat.Position.X / 2.0f + splat.Position.Y).ShouldBeLessThanOrEqualTo(1.0f + 1e-5f);
            splat.Normal.ShouldBe(Vector3.UnitZ);
            Vector3.Transform(Vector3.UnitZ, splat.Rotation).Z.ShouldBe(1.0f, 1e-5f);
            Vector3.Transform(Vector3.UnitX, splat.Rotation).X.ShouldBe(1.0f, 1e-5f);
            splat.Opacity.ShouldBe(0.95f);
            splat.LinearColor.ShouldBe(Vector3.Zero);
            splat.IsStatic.ShouldBeFalse();
        }
    }

    [Test]
    public void Sample_SkipsDegenerateTriangles()
    {
        // Triangle 0 has its three points on one line, so it has no area. Triangle 1 is the only usable one.
        DeformedSurface surface = EvaluateTriangles(
            ([new(0, 0, 0), new(2, 0, 0), new(1, 0, 0), new(0, 2, 0)], [0, 1, 2, 0, 1, 3]));
        surface.TriangleCount.ShouldBe(2);

        BakedSplat[] splats = SurfaceSampler.Sample(surface, 2_000, new BakeRandom(99UL), _meshopt);

        splats.Length.ShouldBe(2_000);
        splats.ShouldAllBe(splat => splat.SourceTriangle == 1);
    }

    [Test]
    public void Sample_ScaleFollowsSpacingRule()
    {
        DeformedSurface surface = EvaluateTriangles(([new(0, 0, 0), new(2, 0, 0), new(0, 1, 0)], [0, 1, 2]));
        const int count = 100;
        float expectedTangent = 0.6f * MathF.Sqrt(1.0f / count);

        BakedSplat[] splats = SurfaceSampler.Sample(surface, count, new BakeRandom(3UL), _meshopt);

        splats.Length.ShouldBe(count);
        foreach (BakedSplat splat in splats)
        {
            splat.Scale.X.ShouldBe(expectedTangent, 1e-6f);
            splat.Scale.Y.ShouldBe(expectedTangent, 1e-6f);
            splat.Scale.Z.ShouldBe(0.02f * expectedTangent, 1e-6f);
        }
    }

    [Test]
    public void Classify_StaticAndAnimatedMeshes()
    {
        SceneNode root = new("Root", new Transform());
        XRMesh first = BakeMotionTestScenes.AddStaticCube(new SceneNode(root, "First", new Transform()));
        BakeMotionTestScenes.AddStaticCube(new SceneNode(root, "Second", new Transform()));
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        DeformedSurface rest = DeformedSurfaceEvaluator.Evaluate(root);
        rest.MeshCount.ShouldBe(2);
        float[] displacement = new float[rest.VertexCount];
        displacement[first.VertexCount + 1] = 0.01f;

        bool[] animated = MotionClassifier.FindAnimatedMeshes(rest, displacement, 0.005f);

        animated.ShouldBe([false, true]);
        displacement[first.VertexCount + 1] = 0.005f;
        MotionClassifier.FindAnimatedMeshes(rest, displacement, 0.005f).ShouldBe([false, false]);
    }

    [Test]
    public void Classify_RigidChildOfAnimatedBone_IsAnimated()
    {
        SceneNode root = GaussianBakeTestAssets.ImportPrefab(GaussianBakeTestAssets.SkinnedAvatarModelPath).RootNode.ShouldNotBeNull();
        List<string> warnings = [];
        BakeMotionChoice choice = BakeMotionSelector.ChooseForPrefab(root, null, GaussianBakeBudget.For(GaussianBakeKind.Avatar), warnings);
        choice.Players.Count.ShouldBe(1);
        SceneNode childJoint = root.FindDescendantByName("ChildJoint").ShouldNotBeNull();
        XRMesh cube = BakeMotionTestScenes.AddStaticCube(new SceneNode(childJoint, "Prop", new Transform(translation: new Vector3(0.0f, 0.5f, 0.0f))));
        DeformedSurface rest = BakeFrameSampler.EvaluateAt(root, choice, 0.0f);
        float[] restPositions = [.. rest.Positions];
        float[] maxDisplacement = new float[rest.VertexCount];

        BakeFrameSampler.ForEachFrame(root, choice, (_, surface) =>
        {
            for (int vertex = 0; vertex < surface.VertexCount; vertex++)
            {
                Vector3 moved = BakeMotionTestScenes.PositionOf(surface.Positions, vertex) - BakeMotionTestScenes.PositionOf(restPositions, vertex);
                maxDisplacement[vertex] = MathF.Max(maxDisplacement[vertex], moved.Length());
            }
        });
        bool[] animated = MotionClassifier.FindAnimatedMeshes(rest, maxDisplacement, 1e-3f);

        int propMesh = Enumerable.Range(0, rest.MeshCount)
            .Single(mesh => rest.MeshVertexStart[mesh + 1] - rest.MeshVertexStart[mesh] == cube.VertexCount);
        animated[propMesh].ShouldBeTrue();
    }

    [Test]
    public void SeedFrom_DiffersByKind()
    {
        ulong avatar = BakeRandom.SeedFrom(SampleHash, GaussianBakeKind.Avatar);
        ulong item = BakeRandom.SeedFrom(SampleHash, GaussianBakeKind.Item);
        ulong world = BakeRandom.SeedFrom(SampleHash, GaussianBakeKind.World);

        avatar.ShouldBe(0x0011223344556677UL ^ 0x9E3779B97F4A7C15UL);
        item.ShouldNotBe(avatar);
        world.ShouldNotBe(avatar);
        world.ShouldNotBe(item);
    }

    [Test]
    public void BakeRandom_FirstOutputMatchesSplitMix64Reference()
    {
        BakeRandom rng = new(0UL);

        rng.NextUInt64().ShouldBe(0xE220A8397B1DCDAFUL);
    }

    [Test]
    public void BakeRandom_NextFloat01_StaysBelowOne()
    {
        BakeRandom rng = new(1UL);

        for (int i = 0; i < 10_000; i++)
        {
            float value = rng.NextFloat01();
            value.ShouldBeGreaterThanOrEqualTo(0.0f);
            value.ShouldBeLessThan(1.0f);
        }
    }

    /// <summary>Makes one mesh for each entry and evaluates the scene at its identity pose.</summary>
    private static DeformedSurface EvaluateTriangles(params (Vector3[] Vertices, ushort[] Triangles)[] meshes)
    {
        SceneNode root = new("Root", new Transform());
        for (int i = 0; i < meshes.Length; i++)
        {
            XRMesh mesh = new(meshes[i].Vertices.Select(position => new Vertex(position)), [.. meshes[i].Triangles]);
            SceneNode node = new(root, $"Mesh{i}", new Transform());
            node.AddComponent<ModelComponent>().ShouldNotBeNull().Model = new Model(new SubMesh(mesh, new XRMaterial()));
        }

        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        return DeformedSurfaceEvaluator.Evaluate(root);
    }
}
