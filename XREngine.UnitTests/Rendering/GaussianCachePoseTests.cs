using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the CPU reference of the two gaussian cache compute passes (driver pose, then cut and splat pose)
/// on the synthetic animated sphere.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GaussianCachePoseTests
{
    private const float FrameRate = GaussianCacheTestSphere.FrameRate;
    private const int FrameCount = GaussianCacheTestSphere.FrameCount;
    private const float PositionTolerance = 1e-5f;

    /// <summary>A small chunk limit, so that the leaf level arrives in more than one chunk.</summary>
    private const int SplitChunkBytes = 16_384;

    private static readonly Vector2 Viewport = new(1920.0f, 1080.0f);
    private static readonly Matrix4x4 Projection = Matrix4x4.CreatePerspectiveFieldOfView(40.0f * MathF.PI / 180.0f, Viewport.X / Viewport.Y, 0.05f, 2_000.0f);

    private GaussianCacheTestSphere _sphere = null!;
    private GaussianCacheBuildResult _loop = null!;

    [OneTimeSetUp]
    public void BuildSphereCache()
    {
        _sphere = GaussianCacheTestSphere.Shared;
        _loop = _sphere.Build();
    }

    [Test]
    public void PoseReference_AtFrameTime_MatchesDecodedFrame()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop);
        GridQuantizer grid = Grid(cache);
        const int frame = 24;

        Vector3[] posed = PoseDrivers(cache, frame / FrameRate);
        for (int vertex = 0; vertex < posed.Length; vertex++)
            Vector3.Distance(posed[vertex], Stored(cache, grid, frame, vertex)).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");

        // Each bound splat sits at its plane coordinates on the stored triangle of the frame.
        List<GaussianSplatPose> splats = Compute(cache, frame / FrameRate, Camera(1.5f), 2.0f);
        splats.Count.ShouldBeGreaterThan(0);
        int bound = 0;
        foreach (GaussianSplatPose splat in splats)
        {
            GaussianCacheSplatRecord record = cache.Runtime.LevelRecords[splat.Level][splat.Node];
            Vector3 expected;
            if ((record.Flags & GaussianCacheSplatRecord.StaticFlag) != 0)
                expected = grid.Dequantize(record.A, record.B, record.C);
            else
            {
                bound++;
                (Vector3 a, Vector3 b, Vector3 c) = Triangle(cache, splat.Level, record.Triangle, vertex => Stored(cache, grid, frame, vertex));
                expected = TriangleFrame.Reconstruct(a, b, c, PlaneCoordinate.Unpack(record.A), PlaneCoordinate.Unpack(record.B), Half(record.C));
                TriangleFrame.TryCompute(a, b, c, out Quaternion triangleFrame).ShouldBeTrue();
                MathF.Abs(Quaternion.Dot(splat.Rotation, Quaternion.Normalize(triangleFrame * Local(record)))).ShouldBeGreaterThan(1.0f - 1e-5f);
            }

            Vector3.Distance(splat.Position, expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"level {splat.Level} node {splat.Node}");
        }

        bound.ShouldBeGreaterThan(0);
    }

    [Test]
    public void PoseReference_BetweenFrames_Interpolates()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop);
        GridQuantizer grid = Grid(cache);

        Vector3[] posed = PoseDrivers(cache, 24.25f / FrameRate);
        for (int vertex = 0; vertex < posed.Length; vertex++)
        {
            Vector3 expected = Vector3.Lerp(Stored(cache, grid, 24, vertex), Stored(cache, grid, 25, vertex), 0.25f);
            Vector3.Distance(posed[vertex], expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");
        }

        // The stored frames differ, so the blend is not one of them.
        MaxDistance(posed, Enumerable.Range(0, posed.Length).Select(vertex => Stored(cache, grid, 24, vertex)).ToArray()).ShouldBeGreaterThan(1e-4f);
    }

    [Test]
    public void PoseReference_OnlyT0Loaded_UsesStep8()
    {
        // Chunk 0 holds splat level 0, driver level 0, and the T0 motion of driver level 0.
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop, chunkCount: 1);
        GaussianCache complete = GaussianCacheTestSphere.Load(_loop);
        GridQuantizer grid = Grid(cache);
        cache.Runtime.Frames.DecodedTemporalLevel[0].ShouldBe((sbyte)0);
        GaussianCachePoseReference.DriverStep(cache.Runtime.Frames, 0).ShouldBe(8);
        GaussianCachePoseReference.DriverStep(cache.Runtime.Frames, 1).ShouldBe(0);

        Vector3[] posed = PoseDrivers(cache, 20.0f / FrameRate);
        int level0 = cache.Runtime.Frames.GetLevelVertexCount(0);
        for (int vertex = 0; vertex < posed.Length; vertex++)
        {
            if (vertex < level0)
            {
                // Frames 16 and 24 are T0 frames. Frame 20 is not loaded, so the pose is their midpoint.
                Vector3 expected = Vector3.Lerp(Stored(complete, grid, 16, vertex), Stored(complete, grid, 24, vertex), 0.5f);
                Vector3.Distance(posed[vertex], expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");
            }
            else
                float.IsNaN(posed[vertex].X).ShouldBeTrue($"vertex {vertex} of a driver level that is not loaded");
        }

        List<GaussianSplatPose> splats = Compute(cache, 20.0f / FrameRate, Camera(1.0f), 0.0f);
        splats.ShouldAllBe(splat => splat.Level == 0);
        splats.Count.ShouldBe(cache.Header.SplatLevelCounts[0]);
    }

    [Test]
    public void PoseReference_LoopWrap_BlendsLastToFirst()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop);
        GridQuantizer grid = Grid(cache);

        // All temporal levels: frame 63.5 blends frame 63 and frame 0.
        Vector3[] posed = PoseDrivers(cache, 63.5f / FrameRate);
        for (int vertex = 0; vertex < posed.Length; vertex++)
        {
            Vector3 expected = Vector3.Lerp(Stored(cache, grid, 63, vertex), Stored(cache, grid, 0, vertex), 0.5f);
            Vector3.Distance(posed[vertex], expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");
        }

        // Only T0: frame 60 blends frame 56 and frame 0.
        GaussianCache t0 = GaussianCacheTestSphere.Load(_loop, chunkCount: 1);
        Vector3[] coarse = PoseDrivers(t0, 60.0f / FrameRate);
        for (int vertex = 0; vertex < t0.Runtime.Frames.GetLevelVertexCount(0); vertex++)
        {
            Vector3 expected = Vector3.Lerp(Stored(cache, grid, 56, vertex), Stored(cache, grid, 0, vertex), 0.5f);
            Vector3.Distance(coarse[vertex], expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");
        }

        // One period later the pose repeats.
        MaxDistance(PoseDrivers(cache, (FrameCount + 63.5f) / FrameRate), posed).ShouldBeLessThanOrEqualTo(PositionTolerance);
        MaxDistance(PoseDrivers(cache, -0.5f / FrameRate), posed).ShouldBeLessThanOrEqualTo(PositionTolerance);
    }

    [Test]
    public void PoseReference_PingPong_ReversesAfterPeriod()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_sphere.Build(GaussianCacheLoopMode.PingPong));
        cache.Header.LoopMode.ShouldBe(GaussianCacheLoopMode.PingPong);
        GridQuantizer grid = Grid(cache);

        // The period ends at the last stored frame, so the pose turns back at frame 63.
        float period = (FrameCount - 1) / FrameRate;
        GaussianCachePoseReference.FractionalFrame(cache.Header, period).ShouldBe(FrameCount - 1, 1e-3f);
        GaussianCachePoseReference.FractionalFrame(cache.Header, period + 3.0f / FrameRate).ShouldBe(FrameCount - 4, 1e-3f);
        GaussianCachePoseReference.FractionalFrame(cache.Header, 2.0f * period + 5.0f / FrameRate).ShouldBe(5.0f, 1e-3f);

        Vector3[] after = PoseDrivers(cache, period + 2.5f / FrameRate);
        Vector3[] before = PoseDrivers(cache, period - 2.5f / FrameRate);
        MaxDistance(after, before).ShouldBeLessThanOrEqualTo(PositionTolerance);
        for (int vertex = 0; vertex < after.Length; vertex++)
        {
            Vector3 expected = Vector3.Lerp(Stored(cache, grid, 60, vertex), Stored(cache, grid, 61, vertex), 0.5f);
            Vector3.Distance(after[vertex], expected).ShouldBeLessThanOrEqualTo(PositionTolerance, $"vertex {vertex}");
        }

        // A loop cache does not turn back: the same time is near frame 2 of the next cycle.
        GaussianCache loop = GaussianCacheTestSphere.Load(_loop);
        MaxDistance(PoseDrivers(loop, period + 2.5f / FrameRate), after).ShouldBeGreaterThan(1e-3f);
    }

    [Test]
    public void PoseReference_CollapsedDriverTriangle_KeepsFinitePose()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop);
        GaussianCacheRuntimeData runtime = cache.Runtime;
        GridQuantizer grid = Grid(cache);
        DriverFrameStore frames = runtime.Frames;
        const int frame = 8;

        // Move every driver vertex of frame 8 to the first vertex, so that every driver triangle collapses.
        ushort[] positions = frames.Positions;
        int frameStart = frame * frames.VertexCount * 3;
        for (int vertex = 1; vertex < frames.VertexCount; vertex++)
            positions.AsSpan(frameStart, 3).CopyTo(positions.AsSpan(frameStart + vertex * 3, 3));
        Vector3 collapsed = Stored(cache, grid, frame, 0);

        List<GaussianSplatPose> splats = Compute(cache, frame / FrameRate, Camera(1.5f), 2.0f);
        splats.Count.ShouldBeGreaterThan(0);
        int bound = 0;
        foreach (GaussianSplatPose splat in splats)
        {
            AssertFinite(splat);
            GaussianCacheSplatRecord record = runtime.LevelRecords[splat.Level][splat.Node];
            if ((record.Flags & GaussianCacheSplatRecord.StaticFlag) != 0)
                continue;

            // The rest frame (frame 0) gives the rotation and the offset from corner a.
            bound++;
            (Vector3 a0, Vector3 b0, Vector3 c0) = Triangle(cache, splat.Level, record.Triangle, vertex => Stored(cache, grid, 0, vertex));
            Vector3 offset = TriangleFrame.Reconstruct(a0, b0, c0, PlaneCoordinate.Unpack(record.A), PlaneCoordinate.Unpack(record.B), Half(record.C)) - a0;
            Vector3.Distance(splat.Position, collapsed + offset).ShouldBeLessThanOrEqualTo(PositionTolerance);
            TriangleFrame.TryCompute(a0, b0, c0, out Quaternion restFrame).ShouldBeTrue();
            MathF.Abs(Quaternion.Dot(splat.Rotation, Quaternion.Normalize(restFrame * Local(record)))).ShouldBeGreaterThan(1.0f - 1e-5f);
        }

        bound.ShouldBeGreaterThan(0);

        // When frame 0 collapses too, the rotation is the local rotation and the splat sits at corner a.
        int restStart = 0;
        for (int vertex = 1; vertex < frames.VertexCount; vertex++)
            positions.AsSpan(restStart, 3).CopyTo(positions.AsSpan(restStart + vertex * 3, 3));
        foreach (GaussianSplatPose splat in Compute(cache, frame / FrameRate, Camera(1.5f), 2.0f))
        {
            AssertFinite(splat);
            GaussianCacheSplatRecord record = runtime.LevelRecords[splat.Level][splat.Node];
            if ((record.Flags & GaussianCacheSplatRecord.StaticFlag) != 0)
                continue;

            Vector3.Distance(splat.Position, collapsed).ShouldBeLessThanOrEqualTo(PositionTolerance);
            MathF.Abs(Quaternion.Dot(splat.Rotation, Local(record))).ShouldBeGreaterThan(1.0f - 1e-5f);
        }
    }

    [Test]
    public void PoseReference_FarCamera_ChoosesCoarserLevel()
    {
        GaussianCache cache = GaussianCacheTestSphere.Load(_loop);

        List<GaussianSplatPose> near = Compute(cache, 0.4f, Camera(1.0f), 2.0f);
        List<GaussianSplatPose> far = Compute(cache, 0.4f, Camera(400.0f), 2.0f);
        TestContext.Out.WriteLine($"Near: {Histogram(near)}. Far: {Histogram(far)}.");

        far.ShouldAllBe(splat => splat.Level == 0);
        far.Count.ShouldBe(cache.Header.SplatLevelCounts[0]);
        near.Max(splat => splat.Level).ShouldBe(cache.Header.SplatLevelCounts.Length - 1);
        near.Count.ShouldBeGreaterThan(far.Count);
        AssertCut(cache, near);
        AssertCut(cache, far);
    }

    [Test]
    public void PoseReference_UnloadedChildren_ChoosesParent()
    {
        GaussianCacheBuildResult split = _sphere.Build(GaussianCacheLoopMode.Loop, SplitChunkBytes);
        GaussianCacheChunkInfo[] infos = split.Root.Header.Chunks;
        int firstLeafChunk = Array.FindIndex(infos, info => info.Kind == GaussianCacheChunkKind.SplatLevel && info.SplatLevel == 2);
        infos[firstLeafChunk + 1].Kind.ShouldBe(GaussianCacheChunkKind.SplatLevel);

        // Accept the first chunk of the leaf level only, so that some leaves are loaded and some are not.
        GaussianCache cache = GaussianCacheTestSphere.Load(split, firstLeafChunk + 1);
        GaussianCacheRuntimeData runtime = cache.Runtime;
        bool[] leaves = runtime.LevelNodeLoaded[2];
        leaves.Count(loaded => loaded).ShouldBeGreaterThan(0);
        leaves.Count(loaded => loaded).ShouldBeLessThan(leaves.Length);

        // A threshold of 0 pixels refines every node whose children are loaded.
        List<GaussianSplatPose> splats = Compute(cache, 0.25f, Camera(1.0f), 0.0f);
        HashSet<(int, int)> chosen = [.. splats.Select(splat => (splat.Level, splat.Node))];
        TestContext.Out.WriteLine($"Partial leaves: {Histogram(splats)}.");

        GaussianCacheSplatRecord[] middle = runtime.LevelRecords[1];
        for (int node = 0; node < middle.Length; node++)
        {
            bool childrenLoaded = leaves[(int)middle[node].ChildStart];
            chosen.Contains((1, node)).ShouldBe(!childrenLoaded, $"level 1 node {node}");
        }

        for (int node = 0; node < leaves.Length; node++)
            chosen.Contains((2, node)).ShouldBe(leaves[node], $"leaf {node}");
        splats.ShouldAllBe(splat => splat.Level > 0);
        AssertCut(cache, splats);
    }

    private static List<GaussianSplatPose> Compute(GaussianCache cache, float time, Matrix4x4 view, float threshold)
    {
        List<GaussianSplatPose> output = [];
        GaussianCachePoseReference.Compute(cache, time, view, Projection, Viewport, threshold, output);
        for (int i = 1; i < output.Count; i++)
            (output[i - 1].Level, output[i - 1].Node).CompareTo((output[i].Level, output[i].Node)).ShouldBeLessThan(0, "The output is sorted by level and node.");
        return output;
    }

    private static Vector3[] PoseDrivers(GaussianCache cache, float time)
    {
        Vector3[] posed = new Vector3[cache.Runtime.Frames.VertexCount];
        GaussianCachePoseReference.PoseDrivers(cache, time, posed);
        return posed;
    }

    /// <summary>Looks at the sphere center from a distance, from the side and a little above.</summary>
    private static Matrix4x4 Camera(float distance)
        => Matrix4x4.CreateLookAt(Vector3.Normalize(new Vector3(1.0f, 0.4f, 0.8f)) * distance, Vector3.Zero, Vector3.UnitY);

    private static GridQuantizer Grid(GaussianCache cache)
        => new(cache.Header.Bounds, cache.Header.GridBits);

    private static Vector3 Stored(GaussianCache cache, GridQuantizer grid, int frame, int vertex)
    {
        DriverFrameStore frames = cache.Runtime.Frames;
        int offset = ((frame * frames.VertexCount) + vertex) * 3;
        return grid.Dequantize(frames.Positions[offset], frames.Positions[offset + 1], frames.Positions[offset + 2]);
    }

    private static (Vector3 A, Vector3 B, Vector3 C) Triangle(GaussianCache cache, int level, uint triangle, Func<int, Vector3> position)
    {
        uint[] indices = cache.Runtime.DriverLevelIndices[level];
        return (position((int)indices[triangle * 3]), position((int)indices[triangle * 3 + 1]), position((int)indices[triangle * 3 + 2]));
    }

    private static Quaternion Local(GaussianCacheSplatRecord record)
        => Quaternion.Normalize(new Quaternion(record.RotX, record.RotY, record.RotZ, record.RotW));

    private static float Half(ushort bits)
        => (float)BitConverter.UInt16BitsToHalf(bits);

    private static float MaxDistance(Vector3[] a, Vector3[] b)
    {
        a.Length.ShouldBe(b.Length);
        float worst = 0.0f;
        for (int i = 0; i < a.Length; i++)
            worst = MathF.Max(worst, Vector3.Distance(a[i], b[i]));
        return worst;
    }

    private static void AssertFinite(GaussianSplatPose splat)
    {
        float.IsFinite(splat.Position.X + splat.Position.Y + splat.Position.Z).ShouldBeTrue($"position of level {splat.Level} node {splat.Node}");
        float.IsFinite(splat.Rotation.X + splat.Rotation.Y + splat.Rotation.Z + splat.Rotation.W).ShouldBeTrue($"rotation of level {splat.Level} node {splat.Node}");
        splat.Rotation.Length().ShouldBe(1.0f, 1e-4f);
    }

    /// <summary>Checks that no chosen node is an ancestor of another chosen node.</summary>
    private static void AssertCut(GaussianCache cache, List<GaussianSplatPose> splats)
    {
        HashSet<(int, int)> chosen = [.. splats.Select(splat => (splat.Level, splat.Node))];
        foreach (GaussianSplatPose splat in splats)
        {
            int level = splat.Level;
            int node = splat.Node;
            while (level > 0)
            {
                node = cache.Runtime.ParentIndex[level][node];
                level--;
                chosen.Contains((level, node)).ShouldBeFalse($"level {splat.Level} node {splat.Node} and its ancestor at level {level} are both chosen");
            }
        }
    }

    private static string Histogram(List<GaussianSplatPose> splats)
        => string.Join(", ", splats.GroupBy(splat => splat.Level).OrderBy(group => group.Key).Select(group => $"L{group.Key}={group.Count()}"));
}
