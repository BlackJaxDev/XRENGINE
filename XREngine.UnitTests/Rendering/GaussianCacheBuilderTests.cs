using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
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
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class GaussianCacheBuilderTests
{
    private const int LeafCount = 20_000;
    private const int FrameCount = 64;
    private const float FrameRate = 30.0f;
    private const int L0Target = 2_000;
    private const float SphereRadius = 0.5f;
    private const int SphereSegments = 96;
    private const int SphereRings = 48;

    /// <summary>Leaves below this height do not move.</summary>
    private const float StaticBelowY = -0.4f;

    /// <summary>A small chunk limit, so that the large levels split over several chunks.</summary>
    private const int SplitChunkBytes = 16_384;

    private static readonly GaussianBakeBudget AvatarBudget = GaussianBakeBudget.For(GaussianBakeKind.Avatar);

    private IMeshOptimizerBackend _meshopt = null!;
    private SphereFixture _sphere = null!;
    private GaussianCacheBuildResult _cache = null!;
    private GaussianCacheBuildResult _splitCache = null!;

    [OneTimeSetUp]
    public void BuildSphereCaches()
    {
        if (MeshOptimizerBackendServices.Current is null)
            MeshOptimizerBackend.Register();
        _meshopt = MeshOptimizerBackendServices.Required;

        _sphere = CreateSphereFixture(_meshopt);
        _cache = GaussianCacheBuilder.Build(_sphere.Input, AvatarBudget, _meshopt);
        _splitCache = GaussianCacheBuilder.Build(_sphere.Input, AvatarBudget, _meshopt, SplitChunkBytes);
    }

    [Test]
    public void Build_EveryChunkAtMostMaxChunkBytes()
    {
        foreach ((GaussianCacheBuildResult result, int limit) in new[] { (_cache, GaussianCacheBuilder.MaxChunkDataBytes), (_splitCache, SplitChunkBytes) })
        {
            WriteLayout(result, limit);
            GaussianCacheHeader header = result.Root.Header;
            header.Chunks.Length.ShouldBe(result.Chunks.Length + 1);
            result.Root.ChunkAssetPaths.Length.ShouldBe(result.Chunks.Length);
            result.Root.RootChunkData.Length.ShouldBeLessThanOrEqualTo(limit);
            CheckChunkInfo(header.Chunks[0], 0, result.Root.RootChunkData);

            long total = HeaderBytes(header).Length + (long)result.Root.RootChunkData.Length;
            for (int i = 1; i <= result.Chunks.Length; i++)
            {
                GaussianCacheChunk chunk = result.Chunks[i - 1];
                chunk.Index.ShouldBe(i);
                chunk.Data.Length.ShouldBeLessThanOrEqualTo(limit);
                chunk.Data.Length.ShouldBeGreaterThan(0);
                result.Root.ChunkAssetPaths[i - 1].ShouldBe($"chunk_{i:D4}.asset");
                CheckChunkInfo(header.Chunks[i], i, chunk.Data);
                total += chunk.Data.Length;
            }

            result.TotalBytes.ShouldBe(total);
            result.TotalBytes.ShouldBeLessThanOrEqualTo(AvatarBudget.MaxCacheBytes);
        }

        // The small limit splits splat sections, triangle sections, and motion sections over more chunks.
        _splitCache.Root.Header.Chunks.Count(info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 2).ShouldBeGreaterThan(1);
        _splitCache.Chunks.Length.ShouldBeGreaterThan(_cache.Chunks.Length);
        List<GaussianCacheSection> sections = AllSections(_splitCache);
        sections.Count(section => section.Kind == GaussianCacheSectionKind.SplatStreams && section.Level == 1).ShouldBeGreaterThan(1);
        sections.Count(section => section.Kind == GaussianCacheSectionKind.DriverIndices && section.Level == 2).ShouldBeGreaterThan(1);
        sections.Count(section => section.Kind == GaussianCacheSectionKind.DriverMotion && section.Level == 2 && section.TemporalLevel == 0).ShouldBeGreaterThan(1);
        sections.Count(section => section.Kind == GaussianCacheSectionKind.DriverMotion && section.Level == 2 && section.TemporalLevel == 3).ShouldBeGreaterThan(1);
    }

    [Test]
    public void Build_Chunk0HoldsHeaderL0D0AndT0Motion()
    {
        GaussianCacheBuildInput input = _sphere.Input;
        GaussianCacheHeader header = _cache.Root.Header;
        IReadOnlyList<GaussianCacheSection> sections = GaussianCacheChunkReader.Read(_cache.Root.RootChunkData);

        sections.Count.ShouldBe(3);
        CheckSection(sections[0], GaussianCacheSectionKind.SplatStreams, 0, 0, 0, input.Tree.Levels[0].Length, 0, 0);
        CheckSection(sections[1], GaussianCacheSectionKind.DriverIndices, 0, 0, 0, input.Drivers.LevelTriangleIndices[0].Length / 3, 0, 0);
        CheckSection(sections[2], GaussianCacheSectionKind.DriverMotion, 0, 0, 0, input.Drivers.CumulativeVertexCounts[0], 0, FrameCount / 8);

        GaussianCacheChunkInfo root = header.Chunks[0];
        root.Kind.ShouldBe(GaussianCacheChunkKind.Root);
        root.SplatLevel.ShouldBe((byte)0);
        root.DriverLevel.ShouldBe((byte)0);
        root.TemporalLevel.ShouldBe((byte)0);

        header.FormatVersion.ShouldBe(GaussianCacheHeader.CurrentFormatVersion);
        header.Representation.ShouldBe(GaussianCacheRepresentation.BakedDriver);
        header.MotionCodecVersion.ShouldBe((ushort)1);
        header.SplatLevelCounts.ShouldBe(input.Tree.Levels.Select(level => level.Length).ToArray());
        header.StaticSplatCounts.ShouldBe(_sphere.StaticCounts);
        header.DriverVertexCounts.ShouldBe(input.Drivers.CumulativeVertexCounts);
        header.DriverTriangleCounts.ShouldBe(input.Drivers.LevelTriangleIndices.Select(indices => indices.Length / 3).ToArray());
        header.FrameCount.ShouldBe(FrameCount);
        header.FrameRate.ShouldBe(FrameRate);
        header.DurationSeconds.ShouldBe(input.Motion.DurationSeconds);
        header.LoopMode.ShouldBe(GaussianCacheLoopMode.Loop);
        header.Bounds.Min.ShouldBe(input.Bounds.Min);
        header.Bounds.Max.ShouldBe(input.Bounds.Max);
        header.GridBits.ShouldBe(input.GridBits);
        header.ColorScale.ShouldBe(input.ColorScale);
        header.CameraHints.ShouldNotBeNull().DefaultRadius.ShouldBe(input.CameraHints!.DefaultRadius);

        _cache.Chunk0Bytes.ShouldBe(HeaderBytes(header).Length + _cache.Root.RootChunkData.Length);
        _cache.Chunk0Bytes.ShouldBeLessThanOrEqualTo(AvatarBudget.Chunk0Bytes);
    }

    [Test]
    public void Build_StreamOrderMatchesSpec()
    {
        (GaussianCacheSectionKind, byte, byte)[] expectedSections =
        [
            (GaussianCacheSectionKind.SplatStreams, 0, 0), (GaussianCacheSectionKind.DriverIndices, 0, 0), (GaussianCacheSectionKind.DriverMotion, 0, 0),
            (GaussianCacheSectionKind.DriverMotion, 0, 1), (GaussianCacheSectionKind.DriverMotion, 0, 2), (GaussianCacheSectionKind.DriverMotion, 0, 3),
            (GaussianCacheSectionKind.DriverIndices, 1, 0), (GaussianCacheSectionKind.DriverMotion, 1, 0), (GaussianCacheSectionKind.SplatStreams, 1, 0),
            (GaussianCacheSectionKind.DriverMotion, 1, 1), (GaussianCacheSectionKind.DriverMotion, 1, 2), (GaussianCacheSectionKind.DriverMotion, 1, 3),
            (GaussianCacheSectionKind.DriverIndices, 2, 0), (GaussianCacheSectionKind.DriverMotion, 2, 0), (GaussianCacheSectionKind.SplatStreams, 2, 0),
            (GaussianCacheSectionKind.DriverMotion, 2, 1), (GaussianCacheSectionKind.DriverMotion, 2, 2), (GaussianCacheSectionKind.DriverMotion, 2, 3),
        ];
        (GaussianCacheChunkKind, byte, byte)[] expectedChunks =
        [
            (GaussianCacheChunkKind.Root, 0, 0),
            (GaussianCacheChunkKind.DriverTemporal, 0, 1), (GaussianCacheChunkKind.DriverTemporal, 0, 2), (GaussianCacheChunkKind.DriverTemporal, 0, 3),
            (GaussianCacheChunkKind.DriverLevel, 1, 0), (GaussianCacheChunkKind.SplatLevel, 1, 0),
            (GaussianCacheChunkKind.DriverTemporal, 1, 1), (GaussianCacheChunkKind.DriverTemporal, 1, 2), (GaussianCacheChunkKind.DriverTemporal, 1, 3),
            (GaussianCacheChunkKind.DriverLevel, 2, 0), (GaussianCacheChunkKind.SplatLevel, 2, 0),
            (GaussianCacheChunkKind.DriverTemporal, 2, 1), (GaussianCacheChunkKind.DriverTemporal, 2, 2), (GaussianCacheChunkKind.DriverTemporal, 2, 3),
        ];

        foreach (GaussianCacheBuildResult result in new[] { _cache, _splitCache })
        {
            List<GaussianCacheSection> sections = AllSections(result);
            Collapse(sections.Select(section => (section.Kind, section.Level, section.TemporalLevel))).ShouldBe(expectedSections);

            GaussianCacheChunkInfo[] chunks = result.Root.Header.Chunks;
            chunks.ShouldAllBe(info => info.SplatLevel == info.DriverLevel);
            Collapse(chunks.Select(info => (info.Kind, info.SplatLevel, info.TemporalLevel))).ShouldBe(expectedChunks);

            CheckSplitBoundaries(sections);
        }
    }

    [Test]
    public void Build_TooManySplats_ThrowsBudgetExceeded()
    {
        BakedSplat[] leaves = CreateStaticLeaves(AvatarBudget.MaxSplats + 1, 1234);
        GaussianCacheBuildInput input = CreateStaticInput(new SplatLodTree([leaves], []), BoundsOf(leaves));

        GaussianBakeException exception = Should.Throw<GaussianBakeException>(() => GaussianCacheBuilder.Build(input, AvatarBudget, _meshopt));

        exception.FailureCode.ShouldBe(GaussianBakeFailureCodes.BudgetExceeded);
        exception.Message.ShouldBe("splats: 50001 > 50000");
    }

    [Test]
    public void Build_Chunk0TooLarge_ThrowsChunk0BudgetExceeded()
    {
        GaussianBakeBudget tight = AvatarBudget with { Chunk0Bytes = 4_096 };

        GaussianBakeException exception = Should.Throw<GaussianBakeException>(() => GaussianCacheBuilder.Build(_sphere.Input, tight, _meshopt));

        exception.FailureCode.ShouldBe(GaussianBakeFailureCodes.BudgetExceeded);
        exception.Message.ShouldStartWith("chunk0: ");
        exception.Message.ShouldEndWith(" > 4096");
    }

    [Test]
    public void Build_IsDeterministic()
    {
        GaussianCacheBuildResult again = GaussianCacheBuilder.Build(_sphere.Input, AvatarBudget, _meshopt);

        again.ContentHash.ShouldBe(_cache.ContentHash);
        again.Root.RootChunkData.ShouldBe(_cache.Root.RootChunkData);
        again.Chunks.Length.ShouldBe(_cache.Chunks.Length);
        for (int i = 0; i < again.Chunks.Length; i++)
            again.Chunks[i].Data.ShouldBe(_cache.Chunks[i].Data);

        // The hash covers the header bytes, then the root chunk data, then each chunk in order.
        using MemoryStream all = new();
        all.Write(HeaderBytes(_cache.Root.Header));
        all.Write(_cache.Root.RootChunkData);
        foreach (GaussianCacheChunk chunk in _cache.Chunks)
            all.Write(chunk.Data);
        _cache.ContentHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(all.ToArray())));
    }

    [Test]
    public void Build_HeaderOnlyChange_ChangesContentHash()
    {
        GaussianCacheCameraHints hints = new() { OrbitTarget = Vector3.One, MinRadius = 1.0f, MaxRadius = 6.0f, DefaultRadius = 2.0f, DefaultPitchDegrees = 30.0f };
        GaussianCacheBuildResult other = GaussianCacheBuilder.Build(CopyInput(_sphere.Input, hints: hints), AvatarBudget, _meshopt);

        other.Root.RootChunkData.ShouldBe(_cache.Root.RootChunkData);
        other.ContentHash.ShouldNotBe(_cache.ContentHash);
    }

    [Test]
    public void Build_BindingCountMismatch_ThrowsArgumentException()
    {
        SplatBinding[][] bindings = [.. _sphere.Input.BindingsPerLevel];
        bindings[2] = bindings[2][..^1];

        Should.Throw<ArgumentException>(() => GaussianCacheBuilder.Build(CopyInput(_sphere.Input, bindings: bindings), AvatarBudget, _meshopt));
    }

    [Test]
    public void Build_StaticOnly_WritesOnlySplatSections()
    {
        BakedSplat[] leaves = CreateStaticLeaves(600, 99);
        SplatLodTree tree = SplatLodTreeBuilder.Build(leaves, 50, _meshopt);
        tree.LevelCount.ShouldBeGreaterThan(1);
        AABB bounds = BoundsOf(tree.Levels.SelectMany(level => level).ToArray());
        GaussianCacheBuildInput input = CreateStaticInput(tree, bounds);

        GaussianCacheBuildResult result = GaussianCacheBuilder.Build(input, AvatarBudget, _meshopt);

        AllSections(result).ShouldAllBe(section => section.Kind == GaussianCacheSectionKind.SplatStreams);
        result.Root.Header.DriverVertexCounts.ShouldBeEmpty();
        result.Root.Header.FrameCount.ShouldBe(1);
        result.Root.Header.StaticSplatCounts.ShouldBe(result.Root.Header.SplatLevelCounts);

        GaussianCache cache = CookRoundTrip(result.Root);
        foreach (GaussianCacheChunk chunk in result.Chunks)
            cache.AcceptChunk(CookRoundTrip(chunk));

        cache.IsComplete.ShouldBeTrue();
        GridQuantizer quantizer = new(input.Bounds, input.GridBits);
        for (int level = 0; level < tree.LevelCount; level++)
        {
            for (int node = 0; node < tree.Levels[level].Length; node++)
            {
                GaussianCacheSplatRecord record = cache.Runtime.LevelRecords[level][node];
                record.Triangle.ShouldBe(GaussianCacheSplatRecord.StaticTriangle);
                quantizer.Quantize(tree.Levels[level][node].Position, out ushort x, out ushort y, out ushort z);
                (record.A, record.B, record.C).ShouldBe((x, y, z));
            }
        }
    }

    [Test]
    public void AcceptAllChunks_ReconstructsFramesAndRecords()
    {
        GaussianCache cache = CookRoundTrip(_splitCache.Root);
        cache.NextChunkIndex.ShouldBe(1);
        cache.IsComplete.ShouldBeFalse();

        foreach (GaussianCacheChunk chunk in _splitCache.Chunks)
            cache.AcceptChunk(CookRoundTrip(chunk));

        cache.IsComplete.ShouldBeTrue();
        cache.NextChunkIndex.ShouldBe(_splitCache.Chunks.Length + 1);
        GaussianCacheRuntimeData runtime = cache.Runtime;
        runtime.Frames.Positions.ShouldBe(_sphere.Input.Frames.Positions);
        runtime.Frames.DecodedTemporalLevel.ShouldAllBe(level => level == 3);
        runtime.LevelNodeLoaded.ShouldAllBe(level => level.All(loaded => loaded));
        CheckDriverTriangles(runtime);
        CheckTreeLinks(runtime);
        CheckRecords(runtime);
    }

    [Test]
    public void AcceptChunk_WrongHash_ThrowsHashMismatch()
    {
        GaussianCache cache = CookRoundTrip(_cache.Root);
        byte[] damaged = [.. _cache.Chunks[0].Data];
        damaged[^1] ^= 0x5A;

        GaussianCacheFormatException exception = Should.Throw<GaussianCacheFormatException>(
            () => cache.AcceptChunk(new GaussianCacheChunk { Index = 1, Data = damaged }));

        exception.DiagnosticCode.ShouldBe(GaussianCacheFormatException.HashMismatchCode);
        cache.NextChunkIndex.ShouldBe(1);
    }

    [Test]
    public void AcceptChunk_OutOfOrder_ThrowsChunkOutOfOrder()
    {
        GaussianCache cache = CookRoundTrip(_cache.Root);

        GaussianCacheFormatException early = Should.Throw<GaussianCacheFormatException>(() => cache.AcceptChunk(_cache.Chunks[1]));
        early.DiagnosticCode.ShouldBe(GaussianCacheFormatException.ChunkOutOfOrderCode);
        cache.NextChunkIndex.ShouldBe(1);

        cache.AcceptChunk(_cache.Chunks[0]);
        GaussianCacheFormatException repeated = Should.Throw<GaussianCacheFormatException>(() => cache.AcceptChunk(_cache.Chunks[0]));
        repeated.DiagnosticCode.ShouldBe(GaussianCacheFormatException.ChunkOutOfOrderCode);
        cache.NextChunkIndex.ShouldBe(2);
    }

    [Test]
    public void AcceptChunk_PartialLevel_MarksOnlyCoveredNodesLoaded()
    {
        GaussianCacheChunkInfo[] infos = _splitCache.Root.Header.Chunks;
        int firstLeafChunk = Array.FindIndex(infos, info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 2);
        firstLeafChunk.ShouldBeGreaterThan(0);
        infos[firstLeafChunk + 1].Kind.ShouldBe(GaussianCacheChunkKind.SplatLevel);

        int firstMiddleChunk = Array.FindIndex(infos, info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 1);
        infos[firstMiddleChunk + 1].Kind.ShouldBe(GaussianCacheChunkKind.SplatLevel);

        GaussianCache cache = CookRoundTrip(_splitCache.Root);
        for (int index = 1; index <= firstLeafChunk; index++)
        {
            cache.AcceptChunk(_splitCache.Chunks[index - 1]);
            if (index != firstMiddleChunk)
                continue;

            // Level 1 is only partly decoded, so only the children of its decoded nodes have a parent index.
            int loadedParents = cache.Runtime.LevelNodeLoaded[1].Count(loaded => loaded);
            loadedParents.ShouldBeGreaterThan(0);
            loadedParents.ShouldBeLessThan(_sphere.Input.Tree.Levels[1].Length);
            int knownChildren = _sphere.Input.Tree.ChildCounts[1].Take(loadedParents).Sum();
            int[] leafParents = cache.Runtime.ParentIndex[2];
            for (int node = 0; node < leafParents.Length; node++)
                (leafParents[node] >= 0).ShouldBe(node < knownChildren, $"leaf {node}");
        }

        IReadOnlyList<GaussianCacheSection> sections = GaussianCacheChunkReader.Read(_splitCache.Chunks[firstLeafChunk - 1].Data);
        sections.ShouldAllBe(section => section.Kind == GaussianCacheSectionKind.SplatStreams && section.Level == 2);
        int covered = sections[^1].First + sections[^1].Count;
        int leafCount = _sphere.Input.Tree.Levels[2].Length;
        covered.ShouldBeGreaterThan(0);
        covered.ShouldBeLessThan(leafCount);

        GaussianCacheRuntimeData runtime = cache.Runtime;
        runtime.LevelNodeLoaded[0].ShouldAllBe(loaded => loaded);
        runtime.LevelNodeLoaded[1].ShouldAllBe(loaded => loaded);
        bool[] leaves = runtime.LevelNodeLoaded[2];
        for (int node = 0; node < leafCount; node++)
            leaves[node].ShouldBe(node < covered, $"leaf {node}");

        // The part ends at a parent boundary, so every parent has all or none of its children.
        int[] parents = runtime.ParentIndex[2];
        parents[covered].ShouldNotBe(parents[covered - 1]);
    }

    [Test]
    public void ScaleFilter_SharedVectorThinNormalAxis_KeepsRelativeErrorBelow2e3()
    {
        const int count = 5_000;
        Random random = new(17);
        float[] input = new float[count * 3];
        for (int i = 0; i < count; i++)
        {
            float tangent = (float)Math.Pow(10.0, -4.0 + 4.0 * random.NextDouble());
            input[i * 3] = tangent;
            input[i * 3 + 1] = tangent * (0.5f + 0.5f * random.NextSingle());
            input[i * 3 + 2] = 0.02f * tangent;
        }

        byte[] buffer = new byte[count * 12];
        _meshopt.EncodeFilterExp(buffer, count, 12, 16, input, MeshOptimizerExpMode.SharedVector);
        _meshopt.DecodeFilterExp(buffer, count, 12);

        ReadOnlySpan<float> decoded = MemoryMarshal.Cast<byte, float>(buffer);
        double worst = 0.0;
        double worstNormal = 0.0;
        for (int i = 0; i < input.Length; i++)
        {
            double relative = Math.Abs(decoded[i] - input[i]) / input[i];
            worst = Math.Max(worst, relative);
            if (i % 3 == 2)
                worstNormal = Math.Max(worstNormal, relative);
        }

        TestContext.Out.WriteLine($"Shared-vector scale: worst relative error {worst:E3}, normal axis {worstNormal:E3}");
        worst.ShouldBeLessThanOrEqualTo(2e-3);
    }

    private sealed record SphereFixture(GaussianCacheBuildInput Input, Vector3[] DriverRest, int[] StaticCounts);

    private static SphereFixture CreateSphereFixture(IMeshOptimizerBackend meshopt)
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
        return new SphereFixture(input, driverRest, staticCounts);
    }

    /// <summary>
    /// Moves a rest point for a frame. The motion is zero at frame 0, repeats every <see cref="FrameCount"/> frames,
    /// and is smaller near the bottom of the sphere.
    /// </summary>
    private static Vector3 Deform(Vector3 rest, int frame)
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

    private static BakedSplat[] CreateStaticLeaves(int count, int seed)
    {
        Random random = new(seed);
        BakedSplat[] leaves = new BakedSplat[count];
        for (int i = 0; i < count; i++)
        {
            leaves[i] = new BakedSplat
            {
                Position = new Vector3(random.NextSingle() * 4.0f, random.NextSingle() * 2.0f, random.NextSingle() * 4.0f),
                Rotation = Quaternion.Normalize(new Quaternion(random.NextSingle(), random.NextSingle(), random.NextSingle(), 1.0f)),
                Scale = new Vector3(0.02f, 0.03f, 0.001f),
                LinearColor = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()),
                Opacity = random.NextSingle(),
                Normal = Vector3.UnitY,
                SourceTriangle = i,
                IsStatic = true,
            };
        }

        return leaves;
    }

    private static AABB BoundsOf(BakedSplat[] splats)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (BakedSplat splat in splats)
        {
            min = Vector3.Min(min, splat.Position);
            max = Vector3.Max(max, splat.Position);
        }

        return new AABB(min, max);
    }

    private static GaussianCacheBuildInput CreateStaticInput(SplatLodTree tree, AABB bounds)
        => new()
        {
            Kind = GaussianBakeKind.Item,
            Tree = tree,
            Drivers = DriverHierarchy.Empty,
            BindingsPerLevel = [],
            DriverRestPositions = [],
            Frames = new DriverFrameStore([], 1, GaussianCacheLoopMode.Once),
            Motion = new BakeMotionChoice([], BakeMotionSources.None, null, 0.0f, 1, FrameRate),
            LoopMode = GaussianCacheLoopMode.Once,
            Bounds = bounds,
            GridBits = 12,
        };

    private static GaussianCacheBuildInput CopyInput(GaussianCacheBuildInput source, SplatBinding[][]? bindings = null, GaussianCacheCameraHints? hints = null)
        => new()
        {
            Kind = source.Kind,
            Tree = source.Tree,
            Drivers = source.Drivers,
            BindingsPerLevel = bindings ?? source.BindingsPerLevel,
            DriverRestPositions = source.DriverRestPositions,
            Frames = source.Frames,
            Motion = source.Motion,
            LoopMode = source.LoopMode,
            Bounds = source.Bounds,
            GridBits = source.GridBits,
            ColorScale = source.ColorScale,
            CameraHints = hints ?? source.CameraHints,
        };

    private static T CookRoundTrip<T>(T asset) where T : XRAsset
    {
        byte[] bytes = CookedBinarySerializer.Serialize(asset);
        return CookedBinarySerializer.Deserialize(typeof(T), bytes).ShouldBeOfType<T>();
    }

    private static byte[] HeaderBytes(GaussianCacheHeader header)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            header.Write(writer);
        return stream.ToArray();
    }

    private static List<GaussianCacheSection> AllSections(GaussianCacheBuildResult result)
    {
        List<GaussianCacheSection> sections = [.. GaussianCacheChunkReader.Read(result.Root.RootChunkData)];
        foreach (GaussianCacheChunk chunk in result.Chunks)
            sections.AddRange(GaussianCacheChunkReader.Read(chunk.Data));
        return sections;
    }

    /// <summary>Removes the repeats that splits make, so that each run of equal items shows once.</summary>
    private static List<T> Collapse<T>(IEnumerable<T> items)
    {
        List<T> result = [];
        foreach (T item in items)
        {
            if (result.Count == 0 || !EqualityComparer<T>.Default.Equals(result[^1], item))
                result.Add(item);
        }

        return result;
    }

    private static void CheckChunkInfo(GaussianCacheChunkInfo info, int index, byte[] data)
    {
        info.Index.ShouldBe(index);
        info.ByteLength.ShouldBe(data.Length);
        info.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    private static void CheckSection(GaussianCacheSection section, GaussianCacheSectionKind kind, byte level, byte temporalLevel, int first, int count, int frameFirst, int frameCount)
    {
        section.Kind.ShouldBe(kind);
        section.Level.ShouldBe(level);
        section.TemporalLevel.ShouldBe(temporalLevel);
        section.First.ShouldBe(first);
        section.Count.ShouldBe(count);
        section.FrameFirst.ShouldBe(frameFirst);
        section.FrameCount.ShouldBe(frameCount);
    }

    /// <summary>Checks that the parts of each split section are contiguous and that splat parts start and end at parent boundaries.</summary>
    private void CheckSplitBoundaries(List<GaussianCacheSection> sections)
    {
        GaussianCacheBuildInput input = _sphere.Input;
        foreach (IGrouping<(GaussianCacheSectionKind, byte, byte), GaussianCacheSection> group in sections.GroupBy(section => (section.Kind, section.Level, section.TemporalLevel)))
        {
            (GaussianCacheSectionKind kind, byte level, byte temporalLevel) = group.Key;
            GaussianCacheSection[] parts = [.. group];
            switch (kind)
            {
                case GaussianCacheSectionKind.SplatStreams:
                {
                    HashSet<int> boundaries = [0];
                    if (level > 0)
                    {
                        int start = 0;
                        foreach (int count in input.Tree.ChildCounts[level - 1])
                            boundaries.Add(start += count);
                    }

                    CheckContiguous(parts.Select(part => (part.First, part.Count)), input.Tree.Levels[level].Length);
                    foreach (GaussianCacheSection part in parts.Skip(1))
                        boundaries.ShouldContain(part.First);
                    break;
                }

                case GaussianCacheSectionKind.DriverIndices:
                    CheckContiguous(parts.Select(part => (part.First, part.Count)), input.Drivers.LevelTriangleIndices[level].Length / 3);
                    break;

                case GaussianCacheSectionKind.DriverMotion:
                {
                    int levelFirst = level == 0 ? 0 : input.Drivers.CumulativeVertexCounts[level - 1];
                    int stride = TemporalLevels.FrameStride(temporalLevel);
                    int nextFrame = TemporalLevels.FirstFrame(temporalLevel);
                    foreach (GaussianCacheSection part in parts)
                    {
                        part.First.ShouldBe(levelFirst);
                        part.Count.ShouldBe(input.Drivers.CumulativeVertexCounts[level] - levelFirst);
                        part.FrameFirst.ShouldBe(nextFrame);
                        nextFrame += part.FrameCount * stride;
                    }

                    parts.Sum(part => part.FrameCount).ShouldBe(TemporalLevels.FramesInLevel(temporalLevel, FrameCount));
                    break;
                }
            }
        }
    }

    private static void CheckContiguous(IEnumerable<(int First, int Count)> parts, int total)
    {
        int next = 0;
        foreach ((int first, int count) in parts)
        {
            first.ShouldBe(next);
            count.ShouldBeGreaterThan(0);
            next += count;
        }

        next.ShouldBe(total);
    }

    /// <summary>
    /// Checks that each decoded triangle list holds the triangles of its level. The index codec can turn the corners of a
    /// triangle, so the check compares each triangle by its smallest rotation.
    /// </summary>
    private void CheckDriverTriangles(GaussianCacheRuntimeData runtime)
    {
        DriverHierarchy drivers = _sphere.Input.Drivers;
        runtime.DriverLevelIndices.Length.ShouldBe(drivers.LevelCount);
        int turned = 0;
        for (int level = 0; level < drivers.LevelCount; level++)
        {
            uint[] decoded = runtime.DriverLevelIndices[level];
            uint[] original = drivers.LevelTriangleIndices[level];
            decoded.Length.ShouldBe(original.Length);
            CanonicalTriangles(decoded).ShouldBe(CanonicalTriangles(original));
            HashSet<(uint, uint, uint)> exact = [.. Triangles(original)];
            turned += Triangles(decoded).Count(triangle => !exact.Contains(triangle));
        }

        TestContext.Out.WriteLine($"Driver triangles with turned corners: {turned}");
        turned.ShouldBeGreaterThan(0);
    }

    private static IEnumerable<(uint, uint, uint)> Triangles(uint[] indices)
    {
        for (int i = 0; i < indices.Length; i += 3)
            yield return (indices[i], indices[i + 1], indices[i + 2]);
    }

    private static List<(uint, uint, uint)> CanonicalTriangles(uint[] indices)
    {
        List<(uint, uint, uint)> result = [];
        foreach ((uint a, uint b, uint c) in Triangles(indices))
        {
            if (a <= b && a <= c)
                result.Add((a, b, c));
            else if (b <= a && b <= c)
                result.Add((b, c, a));
            else
                result.Add((c, a, b));
        }

        result.Sort();
        return result;
    }

    private void CheckTreeLinks(GaussianCacheRuntimeData runtime)
    {
        SplatLodTree tree = _sphere.Input.Tree;
        runtime.ParentIndex[0].ShouldAllBe(parent => parent == -1);
        for (int level = 1; level < tree.LevelCount; level++)
        {
            int[] parents = runtime.ParentIndex[level];
            parents.Length.ShouldBe(tree.Levels[level].Length);
            int child = 0;
            for (int parent = 0; parent < tree.ChildCounts[level - 1].Length; parent++)
            {
                for (int i = 0; i < tree.ChildCounts[level - 1][parent]; i++)
                    parents[child++].ShouldBe(parent);
            }
        }
    }

    private void CheckRecords(GaussianCacheRuntimeData runtime)
    {
        GaussianCacheBuildInput input = _sphere.Input;
        GridQuantizer quantizer = new(input.Bounds, input.GridBits);
        float worstPosition = 0.0f;
        float worstDot = 1.0f;
        float worstScale = 0.0f;
        int worstColor = 0;
        for (int level = 0; level < input.Tree.LevelCount; level++)
        {
            BakedSplat[] splats = input.Tree.Levels[level];
            GaussianCacheSplatRecord[] records = runtime.LevelRecords[level];
            uint[] triangles = runtime.DriverLevelIndices[level];
            records.Length.ShouldBe(splats.Length);
            uint childStart = 0;
            for (int node = 0; node < splats.Length; node++)
            {
                GaussianCacheSplatRecord record = records[node];
                BakedSplat splat = splats[node];
                Quaternion stored = Quaternion.Normalize(new Quaternion(record.RotX, record.RotY, record.RotZ, record.RotW));
                Quaternion rotation;
                if (node < _sphere.StaticCounts[level])
                {
                    record.Triangle.ShouldBe(GaussianCacheSplatRecord.StaticTriangle);
                    (record.Flags & GaussianCacheSplatRecord.StaticFlag).ShouldBe(GaussianCacheSplatRecord.StaticFlag);
                    quantizer.Quantize(splat.Position, out ushort x, out ushort y, out ushort z);
                    (record.A, record.B, record.C).ShouldBe((x, y, z));
                    rotation = stored;
                }
                else
                {
                    (record.Flags & GaussianCacheSplatRecord.StaticFlag).ShouldBe((ushort)0);
                    record.Triangle.ShouldBeLessThan((uint)(triangles.Length / 3));
                    Vector3 a = _sphere.DriverRest[triangles[record.Triangle * 3]];
                    Vector3 b = _sphere.DriverRest[triangles[record.Triangle * 3 + 1]];
                    Vector3 c = _sphere.DriverRest[triangles[record.Triangle * 3 + 2]];
                    float h = (float)BitConverter.UInt16BitsToHalf(record.C);
                    Vector3 center = TriangleFrame.Reconstruct(a, b, c, PlaneCoordinate.Unpack(record.A), PlaneCoordinate.Unpack(record.B), h);
                    worstPosition = MathF.Max(worstPosition, Vector3.Distance(center, splat.Position));
                    TriangleFrame.TryCompute(a, b, c, out Quaternion frame).ShouldBeTrue();
                    rotation = Quaternion.Normalize(frame * stored);
                }

                worstDot = MathF.Min(worstDot, MathF.Abs(Quaternion.Dot(rotation, Quaternion.Normalize(splat.Rotation))));

                Vector3 scale = new(record.ScaleX, record.ScaleY, record.ScaleZ);
                float largest = MathF.Max(splat.Scale.X, MathF.Max(splat.Scale.Y, splat.Scale.Z));
                Vector3 scaleError = Vector3.Abs(scale - splat.Scale) / largest;
                worstScale = MathF.Max(worstScale, MathF.Max(scaleError.X, MathF.Max(scaleError.Y, scaleError.Z)));

                byte[] expectedColor =
                [
                    Srgb8.FromLinear(splat.LinearColor.X), Srgb8.FromLinear(splat.LinearColor.Y), Srgb8.FromLinear(splat.LinearColor.Z),
                    (byte)MathF.Round(Math.Clamp(splat.Opacity, 0.0f, 1.0f) * 255.0f),
                ];
                for (int channel = 0; channel < 4; channel++)
                {
                    int actual = (int)((record.ColorRgba8 >> (channel * 8)) & 0xFF);
                    worstColor = Math.Max(worstColor, Math.Abs(actual - expectedColor[channel]));
                }

                if (level < input.Tree.LevelCount - 1)
                {
                    record.ChildCount.ShouldBe((uint)input.Tree.ChildCounts[level][node]);
                    record.ChildStart.ShouldBe(childStart);
                    childStart += record.ChildCount;
                }
                else
                {
                    record.ChildCount.ShouldBe(0u);
                    record.ChildStart.ShouldBe(0u);
                }
            }
        }

        TestContext.Out.WriteLine(
            $"Record errors: position {worstPosition:E3} m, rotation dot {worstDot:F7}, scale {worstScale:E3} of the largest axis, color {worstColor} levels");
        worstPosition.ShouldBeLessThanOrEqualTo(1e-4f);
        worstDot.ShouldBeGreaterThanOrEqualTo(1.0f - 1e-4f);
        worstScale.ShouldBeLessThanOrEqualTo(1e-4f);
        worstColor.ShouldBeLessThanOrEqualTo(2);
    }

    private void WriteLayout(GaussianCacheBuildResult result, int limit)
    {
        TestContext.Out.WriteLine($"Chunk limit {limit} B: {result.Root.Header.Chunks.Length} chunks, chunk 0 {result.Chunk0Bytes} B, total {result.TotalBytes} B, hash {result.ContentHash}");
        GaussianCacheChunkInfo[] infos = result.Root.Header.Chunks;
        for (int index = 0; index < infos.Length; index++)
        {
            GaussianCacheChunkInfo info = infos[index];
            byte[] data = index == 0 ? result.Root.RootChunkData : result.Chunks[index - 1].Data;
            TestContext.Out.WriteLine($"  chunk {index}: {info.Kind} L{info.SplatLevel} T{info.TemporalLevel} {info.ByteLength} B");
            foreach (GaussianCacheSection section in GaussianCacheChunkReader.Read(data))
            {
                long raw = RawBytes(section);
                TestContext.Out.WriteLine(
                    $"    {section.Kind} level {section.Level} T{section.TemporalLevel} first {section.First} count {section.Count} frames {section.FrameFirst}+{section.FrameCount}: "
                    + $"{section.Payload.Length} B of {raw} B raw ({(double)section.Payload.Length / raw:F3})");
            }
        }
    }

    /// <summary>Returns the size of the section data before compression.</summary>
    private long RawBytes(GaussianCacheSection section)
        => section.Kind switch
        {
            // Binding 8, triangle 4, rotation 8, scale 12 and color 4 bytes, and one child count byte above the leaf level.
            GaussianCacheSectionKind.SplatStreams => (long)section.Count * (36 + (section.Level < _sphere.Input.Tree.LevelCount - 1 ? 1 : 0)),
            GaussianCacheSectionKind.DriverIndices => (long)section.Count * 12,
            _ => (long)section.Count * section.FrameCount * (section.WideResiduals ? 16 : 8),
        };
}
