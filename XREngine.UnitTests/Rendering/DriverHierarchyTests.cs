using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class DriverHierarchyTests
{
    private const float ReconstructTolerance = 1e-5f;
    private const float WeldTolerance = 1e-3f;

    private static readonly GaussianBakeBudget AvatarBudget = GaussianBakeBudget.For(GaussianBakeKind.Avatar);

    private IMeshOptimizerBackend _meshopt = null!;

    [OneTimeSetUp]
    public void InstallMeshProcessing()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;
    }

    [Test]
    public void Build_LevelsAreNestedVertexSubsets()
    {
        DeformedSurface rest = EvaluateMeshes(CreateGrid(100));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);

        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [1_000, 4_000, 16_000], AvatarBudget, _meshopt);

        drivers.LevelCount.ShouldBe(3);
        drivers.CumulativeVertexCounts.Length.ShouldBe(3);
        drivers.CumulativeVertexCounts[^1].ShouldBe(drivers.SourceVertexOfDriverVertex.Length);
        for (int level = 0; level < drivers.LevelCount; level++)
        {
            int levelVertexCount = drivers.CumulativeVertexCounts[level];
            uint[] triangles = drivers.LevelTriangleIndices[level];
            (triangles.Length % 3).ShouldBe(0);
            triangles.Length.ShouldBeGreaterThan(0);
            triangles.Max().ShouldBeLessThan((uint)levelVertexCount);

            // A level uses every driver vertex below its cumulative count, so each level holds all vertices of the levels below it.
            CountUsedVertices(triangles).ShouldBe(levelVertexCount);
            if (level > 0)
                levelVertexCount.ShouldBeGreaterThan(drivers.CumulativeVertexCounts[level - 1]);
        }

        drivers.SourceVertexOfDriverVertex.Distinct().Count().ShouldBe(drivers.SourceVertexOfDriverVertex.Length);
        drivers.SourceVertexOfDriverVertex.ShouldAllBe(source => source >= 0 && source < rest.VertexCount);
    }

    [Test]
    public void Build_TopLevelRespectsMaxDriverVertices()
    {
        DeformedSurface rest = EvaluateMeshes(CreateGrid(200));
        rest.VertexCount.ShouldBe(40_000);
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);

        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [2_000, 8_000, 50_000], AvatarBudget, _meshopt);

        drivers.LevelCount.ShouldBe(3);
        drivers.TargetVertexCounts[^1].ShouldBe(AvatarBudget.MaxDriverVertices);
        CountUsedVertices(drivers.LevelTriangleIndices[^1]).ShouldBeLessThanOrEqualTo(AvatarBudget.MaxDriverVertices);
        drivers.CumulativeVertexCounts[^1].ShouldBeLessThanOrEqualTo(AvatarBudget.MaxDriverVertices);
        drivers.CumulativeVertexCounts[0].ShouldBeGreaterThanOrEqualTo(DriverHierarchyBuilder.MinLevelVertexCount);
    }

    [Test]
    public void Build_TopLevelLandsNearMaxDriverVertices()
    {
        DeformedSurface rest = EvaluateMeshes(CreateGrid(200));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);

        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [2_000, 8_000, 50_000], AvatarBudget, _meshopt);

        for (int level = 0; level < drivers.LevelCount; level++)
        {
            TestContext.Out.WriteLine(
                $"Level {level}: {drivers.CumulativeVertexCounts[level]} vertices (target {drivers.TargetVertexCounts[level]}), "
                + $"{drivers.LevelTriangleIndices[level].Length / 3} triangles");
        }

        // The scaled target keeps the top level near its budget. A halved target would leave about half of the budget unused.
        int topVertexCount = drivers.CumulativeVertexCounts[^1];
        topVertexCount.ShouldBeGreaterThanOrEqualTo((int)(0.85 * AvatarBudget.MaxDriverVertices));
        topVertexCount.ShouldBeLessThanOrEqualTo(AvatarBudget.MaxDriverVertices);
        for (int level = 0; level < drivers.LevelCount; level++)
            drivers.CumulativeVertexCounts[level].ShouldBeLessThanOrEqualTo(drivers.TargetVertexCounts[level]);
    }

    [Test]
    public void SeamWelder_WeldsSplitVerticesThatMoveTogether()
    {
        DeformedSurface rest = EvaluateMeshes(CreateSeamStrip());
        uint[] remap = SeamWelder.CreateRemap(rest, _meshopt);
        remap.ShouldBe([0u, 1u, 2u, 3u, 1u, 5u, 6u, 2u]);

        // The frame turns and moves the full strip, so the two copies of each seam vertex stay together.
        DeformedSurface frame = EvaluateMeshes(CreateSeamStrip());
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        for (int vertex = 0; vertex < frame.VertexCount; vertex++)
            SetPosition(frame, vertex, Vector3.Transform(GetPosition(frame, vertex), turn) + new Vector3(0.25f, -0.5f, 0.1f));

        SeamWelder.BreakDivergent(remap, frame, WeldTolerance);

        remap.ShouldBe([0u, 1u, 2u, 3u, 1u, 5u, 6u, 2u]);
    }

    [Test]
    public void SeamWelder_BreaksWeldWhenAFrameDiverges()
    {
        DeformedSurface rest = EvaluateMeshes(CreateSeamStrip());
        uint[] remap = SeamWelder.CreateRemap(rest, _meshopt);

        // Vertex 7 is the right copy of seam vertex 2 and moves 1 cm away. Vertex 4 moves less than the tolerance.
        DeformedSurface frame = EvaluateMeshes(CreateSeamStrip());
        SetPosition(frame, 7, GetPosition(frame, 7) + new Vector3(0.0f, 0.0f, 0.01f));
        SetPosition(frame, 4, GetPosition(frame, 4) + new Vector3(0.0f, 0.0f, 0.0005f));

        SeamWelder.BreakDivergent(remap, frame, WeldTolerance);

        remap.ShouldBe([0u, 1u, 2u, 3u, 1u, 5u, 6u, 7u]);

        // A later frame where the copies are together again does not weld them again.
        SeamWelder.BreakDivergent(remap, rest, WeldTolerance);
        remap.ShouldBe([0u, 1u, 2u, 3u, 1u, 5u, 6u, 7u]);
    }

    [Test]
    public void Build_WeldedSeams_SimplifyBelowUnweldedVertexCount()
    {
        // Each triangle has its own three vertices, as a mesh with a seam on every edge has.
        DeformedSurface rest = EvaluateMeshes(CreateFacetedGrid(30));
        rest.VertexCount.ShouldBe(5_400);
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        uint[] identity = [.. Enumerable.Range(0, rest.VertexCount).Select(vertex => (uint)vertex)];
        GaussianBakeBudget budget = AvatarBudget with { MaxDriverVertices = 2_000 };

        DriverHierarchy welded = DriverHierarchyBuilder.Build(rest, [true], weld, [1_000], budget, _meshopt);
        DriverHierarchy unwelded = DriverHierarchyBuilder.Build(rest, [true], identity, [1_000], budget, _meshopt);

        // Welded, the 31 × 31 grid points fit the budget, so the driver keeps every triangle.
        welded.CumulativeVertexCounts[^1].ShouldBe(31 * 31);
        welded.CumulativeVertexCounts[^1].ShouldBeLessThan(rest.VertexCount);
        welded.LevelTriangleIndices[^1].Length.ShouldBe(1_800 * 3);
        welded.SourceVertexOfDriverVertex.ShouldAllBe(source => weld[source] == (uint)source);

        // Not welded, the 5,400 split vertices are above the budget, so the driver loses triangles.
        unwelded.CumulativeVertexCounts[^1].ShouldBeLessThanOrEqualTo(budget.MaxDriverVertices);
        unwelded.LevelTriangleIndices[^1].Length.ShouldBeLessThan(1_800 * 3);
    }

    [Test]
    public void Build_NoAnimatedMeshes_HasNoLevels()
    {
        DeformedSurface rest = EvaluateMeshes(CreateSeamStrip(), CreateGrid(4));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);

        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [false, false], weld, [100, 400], AvatarBudget, _meshopt);

        drivers.LevelCount.ShouldBe(0);
        drivers.CumulativeVertexCounts.ShouldBeEmpty();
        drivers.LevelTriangleIndices.ShouldBeEmpty();
        drivers.SourceVertexOfDriverVertex.ShouldBeEmpty();
    }

    [Test]
    public void Bind_ReconstructsRestPositionWithin1e5()
    {
        DeformedSurface rest = EvaluateMeshes(CreateGrid(60));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        GaussianBakeBudget budget = AvatarBudget with { MaxDriverVertices = 600 };
        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [1_000], budget, _meshopt);
        drivers.CumulativeVertexCounts[0].ShouldBeLessThanOrEqualTo(600);
        Vector3[] driverRest = new Vector3[drivers.SourceVertexOfDriverVertex.Length];
        drivers.GatherPositions(rest, driverRest);
        BakedSplat[] splats = SurfaceSampler.Sample(rest, 1_000, new BakeRandom(20_261_026UL), _meshopt);
        splats.Length.ShouldBe(1_000);

        SplatBinding[] bindings = SplatBinder.Bind(splats, drivers, 0, driverRest);

        bindings.Length.ShouldBe(splats.Length);
        uint[] triangles = drivers.LevelTriangleIndices[0];
        float maxError = 0.0f;
        for (int i = 0; i < splats.Length; i++)
        {
            SplatBinding binding = bindings[i];
            ReadTriangle(triangles, driverRest, binding.Triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            TriangleFrame.TryCompute(a, b, c, out Quaternion frame).ShouldBeTrue();

            Vector3 reconstructed = TriangleFrame.Reconstruct(a, b, c, binding.U, binding.V, binding.H);
            maxError = MathF.Max(maxError, Vector3.Distance(reconstructed, splats[i].Position));

            Quaternion rotation = Quaternion.Normalize(frame * binding.LocalRotation);
            MathF.Abs(Quaternion.Dot(rotation, splats[i].Rotation)).ShouldBeGreaterThan(1.0f - 1e-5f);

            // The bound triangle is the closest triangle that has a frame and a good shape.
            float bound = DistanceToTriangle(splats[i].Position, a, b, c);
            bound.ShouldBeLessThanOrEqualTo(ClosestCandidateDistance(splats[i].Position, triangles, driverRest) + 1e-6f);
        }

        TestContext.Out.WriteLine($"Max reconstruct error: {maxError:E3} m over {splats.Length} splats and {triangles.Length / 3} driver triangles");
        maxError.ShouldBeLessThanOrEqualTo(ReconstructTolerance);
    }

    [Test]
    public void Bind_NeverPicksDegenerateTriangle()
    {
        // Triangle 0 has its three points on one line, so it has no frame. The splats are nearer to it than to triangle 1.
        DeformedSurface rest = EvaluateMeshes((
            [new(0, 0, 0), new(2, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 1, 0), new(0, 3, 0)],
            [0, 1, 2, 3, 4, 5]));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [20], AvatarBudget, _meshopt);
        uint[] triangles = drivers.LevelTriangleIndices[0];
        triangles.Length.ShouldBe(6);
        Vector3[] driverRest = new Vector3[drivers.SourceVertexOfDriverVertex.Length];
        drivers.GatherPositions(rest, driverRest);
        int degenerateCount = 0;
        for (uint triangle = 0; triangle < 2; triangle++)
        {
            ReadTriangle(triangles, driverRest, triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            if (!TriangleFrame.TryCompute(a, b, c, out _))
                degenerateCount++;
        }

        degenerateCount.ShouldBe(1);
        BakedSplat[] splats = new BakedSplat[20];
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f);
        for (int i = 0; i < splats.Length; i++)
            splats[i] = new BakedSplat { Position = new Vector3(0.1f + 0.09f * i, -0.2f, 0.05f), Rotation = tilt, Scale = new Vector3(0.01f) };

        SplatBinding[] bindings = SplatBinder.Bind(splats, drivers, 0, driverRest);

        for (int i = 0; i < splats.Length; i++)
        {
            ReadTriangle(triangles, driverRest, bindings[i].Triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            TriangleFrame.TryCompute(a, b, c, out _).ShouldBeTrue();
            Vector3 reconstructed = TriangleFrame.Reconstruct(a, b, c, bindings[i].U, bindings[i].V, bindings[i].H);
            Vector3.Distance(reconstructed, splats[i].Position).ShouldBeLessThanOrEqualTo(ReconstructTolerance);
        }
    }

    [Test]
    public void Bind_SliverIsClosest_PicksWellShapedTriangle()
    {
        // Triangle 0 is a sliver in the plane z = 0: its height is 1e-4 of its longest edge. Triangle 1 is well shaped
        // and lies in the plane z = 0.0115. The splats are at z = 0.005, so triangle 1 is 1.5 mm farther from them.
        DeformedSurface rest = EvaluateMeshes((
            [new(0, 0, 0), new(1, 0, 0), new(0.5f, 1e-4f, 0), new(-1, -1, 0.0115f), new(2, -1, 0.0115f), new(0.5f, 2, 0.0115f)],
            [0, 1, 2, 3, 4, 5]));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [10], AvatarBudget, _meshopt);
        uint[] triangles = drivers.LevelTriangleIndices[0];
        triangles.Length.ShouldBe(6);
        Vector3[] driverRest = new Vector3[drivers.SourceVertexOfDriverVertex.Length];
        drivers.GatherPositions(rest, driverRest);
        BakedSplat[] splats = new BakedSplat[10];
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 0.7f);
        for (int i = 0; i < splats.Length; i++)
            splats[i] = new BakedSplat { Position = new Vector3(0.3f + 0.04f * i, 2e-5f, 0.005f), Rotation = tilt, Scale = new Vector3(0.01f) };

        uint sliver = FindTriangle(triangles, driverRest, ratio => ratio < SplatBinder.MinTriangleShapeRatio);
        uint wellShaped = FindTriangle(triangles, driverRest, ratio => ratio >= SplatBinder.MinTriangleShapeRatio);
        ReadTriangle(triangles, driverRest, sliver, out Vector3 sa, out Vector3 sb, out Vector3 sc);
        ReadTriangle(triangles, driverRest, wellShaped, out Vector3 wa, out Vector3 wb, out Vector3 wc);
        TriangleFrame.TryCompute(sa, sb, sc, out _).ShouldBeTrue();
        foreach (BakedSplat splat in splats)
            DistanceToTriangle(splat.Position, sa, sb, sc).ShouldBeLessThan(DistanceToTriangle(splat.Position, wa, wb, wc));

        SplatBinding[] bindings = SplatBinder.Bind(splats, drivers, 0, driverRest);

        for (int i = 0; i < splats.Length; i++)
        {
            bindings[i].Triangle.ShouldBe(wellShaped);
            Vector3 reconstructed = TriangleFrame.Reconstruct(wa, wb, wc, bindings[i].U, bindings[i].V, bindings[i].H);
            Vector3.Distance(reconstructed, splats[i].Position).ShouldBeLessThanOrEqualTo(ReconstructTolerance);
        }
    }

    [Test]
    public void Bind_AllTrianglesAreSlivers_FallsBackToFramedTriangles()
    {
        // Both triangles are slivers with a height of 1e-4 of the longest edge, but both have a frame.
        DeformedSurface rest = EvaluateMeshes((
            [new(0, 0, 0), new(1, 0, 0), new(0.5f, 1e-4f, 0), new(0, 0.5f, 0), new(1, 0.5f, 0), new(0.5f, 0.5001f, 0)],
            [0, 1, 2, 3, 4, 5]));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [16], AvatarBudget, _meshopt);
        uint[] triangles = drivers.LevelTriangleIndices[0];
        triangles.Length.ShouldBe(6);
        Vector3[] driverRest = new Vector3[drivers.SourceVertexOfDriverVertex.Length];
        drivers.GatherPositions(rest, driverRest);
        BakedSplat[] splats = new BakedSplat[16];
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-2, 1, 3)), 1.1f);
        for (int i = 0; i < splats.Length; i++)
        {
            // Some centers are off the sliver in its plane by up to 9 sliver heights, so v reaches about 9.
            float y = (i % 2 == 0 ? 0.0f : 0.5f) + 3e-4f * (i % 4);
            splats[i] = new BakedSplat { Position = new Vector3(0.2f + 0.04f * i, y, i % 3 == 0 ? -0.01f : 0.01f), Rotation = tilt, Scale = new Vector3(0.01f) };
        }

        SplatBinding[] bindings = SplatBinder.Bind(splats, drivers, 0, driverRest);

        for (int i = 0; i < splats.Length; i++)
        {
            SplatBinding binding = bindings[i];
            ReadTriangle(triangles, driverRest, binding.Triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            ShapeRatio(a, b, c).ShouldBeLessThan(SplatBinder.MinTriangleShapeRatio);
            TriangleFrame.TryCompute(a, b, c, out Quaternion frame).ShouldBeTrue();
            float.IsFinite(binding.U).ShouldBeTrue();
            float.IsFinite(binding.V).ShouldBeTrue();
            float.IsFinite(binding.H).ShouldBeTrue();
            Vector3 reconstructed = TriangleFrame.Reconstruct(a, b, c, binding.U, binding.V, binding.H);
            Vector3.Distance(reconstructed, splats[i].Position).ShouldBeLessThanOrEqualTo(ReconstructTolerance);
            MathF.Abs(Quaternion.Dot(Quaternion.Normalize(frame * binding.LocalRotation), tilt)).ShouldBeGreaterThan(1.0f - 1e-5f);
        }
    }

    [Test]
    public void Bind_FarFromOrigin_DoesNotFailOnPositionRounding()
    {
        // At 1,000 m one float step is 6.1e-5 m, so a world-space reconstruction cannot be within 1e-5 m of every
        // center. The binder checks the offset from the triangle corner instead.
        (Vector3[] vertices, ushort[] gridTriangles) = CreateGrid(20);
        Vector3 offset = new(1_000.0f, -500.0f, 200.0f);
        DeformedSurface rest = EvaluateMeshes(([.. vertices.Select(vertex => vertex + offset)], gridTriangles));
        uint[] weld = SeamWelder.CreateRemap(rest, _meshopt);
        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [200], AvatarBudget, _meshopt);
        Vector3[] driverRest = new Vector3[drivers.SourceVertexOfDriverVertex.Length];
        drivers.GatherPositions(rest, driverRest);
        BakedSplat[] splats = SurfaceSampler.Sample(rest, 200, new BakeRandom(7UL), _meshopt);

        SplatBinding[] bindings = SplatBinder.Bind(splats, drivers, 0, driverRest);

        uint[] triangles = drivers.LevelTriangleIndices[0];
        float maxWorldError = 0.0f;
        float maxOffsetError = 0.0f;
        for (int i = 0; i < splats.Length; i++)
        {
            ReadTriangle(triangles, driverRest, bindings[i].Triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            Vector3 world = TriangleFrame.Reconstruct(a, b, c, bindings[i].U, bindings[i].V, bindings[i].H);
            Vector3 local = TriangleFrame.Reconstruct(Vector3.Zero, b - a, c - a, bindings[i].U, bindings[i].V, bindings[i].H);
            maxWorldError = MathF.Max(maxWorldError, Vector3.Distance(world, splats[i].Position));
            maxOffsetError = MathF.Max(maxOffsetError, Vector3.Distance(local, splats[i].Position - a));
        }

        TestContext.Out.WriteLine($"Far from origin: max world error {maxWorldError:E3} m, max offset error {maxOffsetError:E3} m");
        maxOffsetError.ShouldBeLessThanOrEqualTo(SplatBinder.MaxReconstructErrorMeters);
        maxWorldError.ShouldBeLessThanOrEqualTo(4.0f * (MathF.BitIncrement(1_024.0f) - 1_024.0f));
    }

    /// <summary>A dome over the unit square, so the surface has curvature in both directions.</summary>
    private static float Height(float x, float y)
        => 0.25f * MathF.Sin(MathF.PI * x) * MathF.Sin(MathF.PI * y);

    /// <summary>Makes an n × n grid of shared vertices over the unit square, with the height of <see cref="Height"/>.</summary>
    private static (Vector3[] Vertices, ushort[] Triangles) CreateGrid(int n)
    {
        Vector3[] vertices = new Vector3[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float u = x / (float)(n - 1);
                float v = y / (float)(n - 1);
                vertices[y * n + x] = new Vector3(u, v, Height(u, v));
            }
        }

        List<ushort> triangles = new((n - 1) * (n - 1) * 6);
        for (int y = 0; y < n - 1; y++)
        {
            for (int x = 0; x < n - 1; x++)
            {
                ushort v00 = (ushort)(y * n + x);
                ushort v10 = (ushort)(v00 + 1);
                ushort v01 = (ushort)(v00 + n);
                ushort v11 = (ushort)(v01 + 1);
                triangles.AddRange([v00, v10, v11, v00, v11, v01]);
            }
        }

        return (vertices, [.. triangles]);
    }

    /// <summary>Makes the triangles of a grid with <paramref name="cells"/> × <paramref name="cells"/> cells, where each triangle has its own vertices.</summary>
    private static (Vector3[] Vertices, ushort[] Triangles) CreateFacetedGrid(int cells)
    {
        (Vector3[] gridVertices, ushort[] gridTriangles) = CreateGrid(cells + 1);
        Vector3[] vertices = new Vector3[gridTriangles.Length];
        ushort[] triangles = new ushort[gridTriangles.Length];
        for (int i = 0; i < gridTriangles.Length; i++)
        {
            vertices[i] = gridVertices[gridTriangles[i]];
            triangles[i] = (ushort)i;
        }

        return (vertices, triangles);
    }

    /// <summary>
    /// Makes two quads that share the edge from (1, 0) to (1, 1). The right quad has its own copies of the edge vertices:
    /// vertex 4 is at the position of vertex 1, and vertex 7 is at the position of vertex 2.
    /// </summary>
    private static (Vector3[] Vertices, ushort[] Triangles) CreateSeamStrip()
        => ([
                new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
                new(1, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0),
            ],
            [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);

    /// <summary>Makes one mesh for each entry and evaluates the scene at its identity pose.</summary>
    private static DeformedSurface EvaluateMeshes(params (Vector3[] Vertices, ushort[] Triangles)[] meshes)
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

    private static int CountUsedVertices(uint[] triangles)
        => triangles.Distinct().Count();

    private static Vector3 GetPosition(DeformedSurface surface, int vertex)
        => new(surface.Positions[vertex * 3], surface.Positions[vertex * 3 + 1], surface.Positions[vertex * 3 + 2]);

    private static void SetPosition(DeformedSurface surface, int vertex, Vector3 position)
    {
        surface.Positions[vertex * 3] = position.X;
        surface.Positions[vertex * 3 + 1] = position.Y;
        surface.Positions[vertex * 3 + 2] = position.Z;
    }

    private static void ReadTriangle(uint[] triangles, Vector3[] positions, uint triangle, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = positions[triangles[triangle * 3]];
        b = positions[triangles[triangle * 3 + 1]];
        c = positions[triangles[triangle * 3 + 2]];
    }

    /// <summary>
    /// Returns the smallest distance from the point to a triangle of the list that has a frame and a good shape, by a
    /// test of every triangle.
    /// </summary>
    private static float ClosestCandidateDistance(Vector3 point, uint[] triangles, Vector3[] positions)
    {
        float closest = float.PositiveInfinity;
        for (uint triangle = 0; triangle < triangles.Length / 3; triangle++)
        {
            ReadTriangle(triangles, positions, triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            if (TriangleFrame.TryCompute(a, b, c, out _) && ShapeRatio(a, b, c) >= SplatBinder.MinTriangleShapeRatio)
                closest = MathF.Min(closest, DistanceToTriangle(point, a, b, c));
        }

        return closest;
    }

    /// <summary>Returns the first triangle of the list whose shape ratio matches.</summary>
    private static uint FindTriangle(uint[] triangles, Vector3[] positions, Func<double, bool> match)
    {
        for (uint triangle = 0; triangle < triangles.Length / 3; triangle++)
        {
            ReadTriangle(triangles, positions, triangle, out Vector3 a, out Vector3 b, out Vector3 c);
            if (match(ShapeRatio(a, b, c)))
                return triangle;
        }

        throw new InvalidOperationException("No triangle of the list has a matching shape ratio.");
    }

    /// <summary>Returns |ab × ac| / (longest edge)², in double precision.</summary>
    private static double ShapeRatio(Vector3 a, Vector3 b, Vector3 c)
    {
        double abX = b.X - (double)a.X, abY = b.Y - (double)a.Y, abZ = b.Z - (double)a.Z;
        double acX = c.X - (double)a.X, acY = c.Y - (double)a.Y, acZ = c.Z - (double)a.Z;
        double bcX = acX - abX, bcY = acY - abY, bcZ = acZ - abZ;
        double crossX = abY * acZ - abZ * acY;
        double crossY = abZ * acX - abX * acZ;
        double crossZ = abX * acY - abY * acX;
        double longest = Math.Max(abX * abX + abY * abY + abZ * abZ, Math.Max(acX * acX + acY * acY + acZ * acZ, bcX * bcX + bcY * bcY + bcZ * bcZ));
        return Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ) / longest;
    }

    /// <summary>Returns the distance from the point to the closest point of the triangle, by a projection with clamped barycentric coordinates.</summary>
    private static float DistanceToTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        float best = MathF.Min(DistanceToSegment(point, a, b), MathF.Min(DistanceToSegment(point, b, c), DistanceToSegment(point, c, a)));
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        float height = Vector3.Dot(point - a, normal);
        Vector3 projected = point - height * normal;
        bool inside = Vector3.Dot(Vector3.Cross(b - a, projected - a), normal) >= 0.0f
            && Vector3.Dot(Vector3.Cross(c - b, projected - b), normal) >= 0.0f
            && Vector3.Dot(Vector3.Cross(a - c, projected - c), normal) >= 0.0f;
        return inside ? MathF.Min(best, MathF.Abs(height)) : best;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 segment = end - start;
        float t = Math.Clamp(Vector3.Dot(point - start, segment) / segment.LengthSquared(), 0.0f, 1.0f);
        return Vector3.Distance(point, start + t * segment);
    }
}
