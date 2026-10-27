using System.Numerics;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Lod;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The synthetic animated sphere that the gaussian cache tests build. The sphere has 20,000 leaves in three
/// levels and 64 frames of motion. The leaves below <see cref="StaticBelowY"/> do not move.
/// </summary>
/// <param name="Input">The input of <see cref="GaussianCacheBuilder.Build(GaussianCacheBuildInput, GaussianBakeBudget, IMeshOptimizerBackend)"/>.</param>
/// <param name="DriverRest">The rest positions of the driver vertices.</param>
/// <param name="StaticCounts">The number of static splats in each level.</param>
internal sealed record GaussianCacheTestSphere(GaussianCacheBuildInput Input, Vector3[] DriverRest, int[] StaticCounts)
{
    public const int LeafCount = 20_000;
    public const int FrameCount = 64;
    public const float FrameRate = 30.0f;
    public const int L0Target = 2_000;
    public const float SphereRadius = 0.5f;
    public const int SphereSegments = 96;
    public const int SphereRings = 48;

    /// <summary>Leaves below this height do not move.</summary>
    public const float StaticBelowY = -0.4f;

    public static readonly GaussianBakeBudget AvatarBudget = GaussianBakeBudget.For(GaussianBakeKind.Avatar);

    private static readonly object SharedSync = new();
    private static GaussianCacheTestSphere? s_shared;
    private readonly Dictionary<(GaussianCacheLoopMode, int), GaussianCacheBuildResult> _builds = [];

    /// <summary>The installed meshoptimizer backend. The first call installs it.</summary>
    public static IMeshOptimizerBackend Meshopt
    {
        get
        {
            if (MeshOptimizerBackendServices.Current is null)
                MeshOptimizerBackend.Register();
            return MeshOptimizerBackendServices.Required;
        }
    }

    /// <summary>One sphere for all fixtures in the test run. The first call builds it.</summary>
    public static GaussianCacheTestSphere Shared
    {
        get
        {
            lock (SharedSync)
                return s_shared ??= Create(Meshopt);
        }
    }

    /// <summary>Builds the cache of the sphere with a loop mode and a chunk data limit, one time for each pair.</summary>
    public GaussianCacheBuildResult Build(GaussianCacheLoopMode loopMode = GaussianCacheLoopMode.Loop, int maxChunkDataBytes = GaussianCacheBuilder.MaxChunkDataBytes)
    {
        lock (SharedSync)
        {
            if (!_builds.TryGetValue((loopMode, maxChunkDataBytes), out GaussianCacheBuildResult? result))
            {
                GaussianCacheBuildInput input = loopMode == Input.LoopMode ? Input : WithLoopMode(loopMode);
                result = GaussianCacheBuilder.Build(input, AvatarBudget, Meshopt, maxChunkDataBytes);
                _builds.Add((loopMode, maxChunkDataBytes), result);
            }

            return result;
        }
    }

    /// <summary>Makes a new cache from a build result and accepts the chunks before <paramref name="chunkCount"/>. Chunk 0 counts as one chunk.</summary>
    public static GaussianCache Load(GaussianCacheBuildResult result, int chunkCount = int.MaxValue)
    {
        GaussianCache cache = new()
        {
            Header = result.Root.Header,
            RootChunkData = result.Root.RootChunkData,
            ChunkAssetPaths = result.Root.ChunkAssetPaths,
        };
        _ = cache.Runtime;
        int last = Math.Min(chunkCount, result.Chunks.Length + 1);
        for (int index = 1; index < last; index++)
            cache.AcceptChunk(result.Chunks[index - 1]);
        return cache;
    }

    /// <summary>Samples, binds, and quantizes the sphere. The result is the same for each call.</summary>
    public static GaussianCacheTestSphere Create(IMeshOptimizerBackend meshopt)
    {
        DeformedSurface rest = EvaluateMeshes(CreateUvSphere());
        uint[] weld = SeamWelder.CreateRemap(rest, meshopt);
        BakedSplat[] leaves = SurfaceSampler.Sample(rest, LeafCount, new BakeRandom(20_261_026UL), meshopt);
        for (int i = 0; i < leaves.Length; i++)
        {
            Vector3 p = leaves[i].Position / SphereRadius;
            leaves[i].IsStatic = p.Y < StaticBelowY / SphereRadius;
            leaves[i].LinearColor = new Vector3(0.1f + 0.4f * (p.X + 1.0f), 0.5f + 0.3f * p.Y, 0.2f + 0.35f * (p.Z + 1.0f));
            leaves[i].Opacity = 0.55f + 0.2f * (p.X * p.Z + 1.0f);
        }

        SplatLodTree tree = SplatLodTreeBuilder.Build(leaves, L0Target, meshopt);
        tree.LevelCount.ShouldBe(3);
        int[] staticCounts = [.. tree.Levels.Select(level => level.Count(splat => splat.IsStatic))];

        DriverHierarchy drivers = DriverHierarchyBuilder.Build(rest, [true], weld, [.. tree.Levels.Select(level => level.Length)], AvatarBudget, meshopt);
        drivers.LevelCount.ShouldBe(3);
        Vector3[] driverRest = new Vector3[drivers.VertexCount];
        drivers.GatherPositions(rest, driverRest);

        SplatBinding[][] bindings = new SplatBinding[tree.LevelCount][];
        for (int level = 0; level < tree.LevelCount; level++)
            bindings[level] = SplatBinder.Bind(tree.Levels[level].AsSpan(staticCounts[level]), drivers, level, driverRest);

        // The bounds hold every driver vertex in every frame and every static splat.
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int frame = 0; frame < FrameCount; frame++)
        {
            foreach (Vector3 vertex in driverRest)
            {
                Vector3 p = Deform(vertex, frame);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        foreach (BakedSplat splat in tree.Levels.SelectMany(level => level).Where(splat => splat.IsStatic))
        {
            min = Vector3.Min(min, splat.Position);
            max = Vector3.Max(max, splat.Position);
        }

        AABB bounds = new(min - new Vector3(1e-3f), max + new Vector3(1e-3f));
        byte gridBits = GridQuantizer.ChooseGridBits(bounds, AvatarBudget.MaxPositionErrorMeters);
        GridQuantizer quantizer = new(bounds, gridBits);
        DriverFrameStore frames = new(drivers.CumulativeVertexCounts, FrameCount, GaussianCacheLoopMode.Loop);
        for (int frame = 0; frame < FrameCount; frame++)
        {
            for (int vertex = 0; vertex < driverRest.Length; vertex++)
            {
                quantizer.Quantize(Deform(driverRest[vertex], frame), out ushort x, out ushort y, out ushort z);
                int offset = ((frame * driverRest.Length) + vertex) * 3;
                frames.Positions[offset] = x;
                frames.Positions[offset + 1] = y;
                frames.Positions[offset + 2] = z;
            }
        }

        GaussianCacheBuildInput input = new()
        {
            Kind = GaussianBakeKind.Avatar,
            Tree = tree,
            Drivers = drivers,
            BindingsPerLevel = bindings,
            DriverRestPositions = driverRest,
            Frames = frames,
            Motion = new BakeMotionChoice([], BakeMotionSources.First, "wobble", FrameCount / FrameRate, FrameCount, FrameRate),
            LoopMode = GaussianCacheLoopMode.Loop,
            Bounds = bounds,
            GridBits = gridBits,
            ColorScale = 1.25f,
            CameraHints = new GaussianCacheCameraHints { MinRadius = 0.6f, MaxRadius = 3.6f, DefaultRadius = 1.2f, DefaultPitchDegrees = 15.0f },
        };
        return new GaussianCacheTestSphere(input, driverRest, staticCounts);
    }

    /// <summary>Returns a copy of the input with another loop mode. The frames are copied into a store with that loop mode.</summary>
    public GaussianCacheBuildInput WithLoopMode(GaussianCacheLoopMode loopMode)
    {
        DriverFrameStore frames = new(Input.Drivers.CumulativeVertexCounts, FrameCount, loopMode);
        Input.Frames.Positions.CopyTo(frames.Positions, 0);
        return new GaussianCacheBuildInput
        {
            Kind = Input.Kind,
            Tree = Input.Tree,
            Drivers = Input.Drivers,
            BindingsPerLevel = Input.BindingsPerLevel,
            DriverRestPositions = Input.DriverRestPositions,
            Frames = frames,
            Motion = Input.Motion,
            LoopMode = loopMode,
            Bounds = Input.Bounds,
            GridBits = Input.GridBits,
            ColorScale = Input.ColorScale,
            CameraHints = Input.CameraHints,
        };
    }

    /// <summary>
    /// Moves a rest point for a frame. The motion is zero at frame 0, repeats every <see cref="FrameCount"/> frames,
    /// and is smaller near the bottom of the sphere.
    /// </summary>
    public static Vector3 Deform(Vector3 rest, int frame)
    {
        float phase = 2.0f * MathF.PI * frame / FrameCount;
        float weight = (rest.Y / SphereRadius + 1.0f) * 0.5f;
        Vector3 radial = Vector3.Normalize(rest);
        Vector3 offset = 0.04f * MathF.Sin(phase) * radial
            + 0.03f * MathF.Sin(phase) * MathF.Cos(4.0f * rest.Y) * Vector3.UnitX
            + 0.02f * MathF.Sin(2.0f * phase) * Vector3.UnitZ;
        return rest + weight * offset;
    }

    /// <summary>Makes a sphere with shared vertices: two poles and <see cref="SphereRings"/> − 1 rings of <see cref="SphereSegments"/> vertices.</summary>
    private static (Vector3[] Vertices, ushort[] Triangles) CreateUvSphere()
    {
        List<Vector3> vertices = [new Vector3(0.0f, SphereRadius, 0.0f)];
        for (int ring = 1; ring < SphereRings; ring++)
        {
            float polar = MathF.PI * ring / SphereRings;
            for (int segment = 0; segment < SphereSegments; segment++)
            {
                float azimuth = 2.0f * MathF.PI * segment / SphereSegments;
                vertices.Add(SphereRadius * new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth)));
            }
        }

        vertices.Add(new Vector3(0.0f, -SphereRadius, 0.0f));
        ushort south = (ushort)(vertices.Count - 1);
        List<ushort> triangles = [];
        for (int segment = 0; segment < SphereSegments; segment++)
        {
            int next = (segment + 1) % SphereSegments;
            triangles.AddRange([0, (ushort)(1 + next), (ushort)(1 + segment)]);
            for (int ring = 0; ring < SphereRings - 2; ring++)
            {
                ushort a = (ushort)(1 + ring * SphereSegments + segment);
                ushort b = (ushort)(1 + ring * SphereSegments + next);
                ushort c = (ushort)(a + SphereSegments);
                ushort d = (ushort)(b + SphereSegments);
                triangles.AddRange([a, b, d, a, d, c]);
            }

            int lastRing = 1 + (SphereRings - 2) * SphereSegments;
            triangles.AddRange([(ushort)(lastRing + segment), (ushort)(lastRing + next), south]);
        }

        return ([.. vertices], [.. triangles]);
    }

    private static DeformedSurface EvaluateMeshes((Vector3[] Vertices, ushort[] Triangles) mesh)
    {
        SceneNode root = new("Root", new Transform());
        XRMesh xrMesh = new(mesh.Vertices.Select(position => new Vertex(position)), [.. mesh.Triangles]);
        SceneNode node = new(root, "Sphere", new Transform());
        node.AddComponent<ModelComponent>().ShouldNotBeNull().Model = new Model(new SubMesh(xrMesh, new XRMaterial()));
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        return DeformedSurfaceEvaluator.Evaluate(root);
    }
}
