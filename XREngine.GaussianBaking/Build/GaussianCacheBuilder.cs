using System.Globalization;
using System.Security.Cryptography;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

/// <summary>
/// Writes a progressive gaussian cache: the root asset, which holds the header and chunk 0, and the chunks after it.
/// </summary>
/// <remarks>
/// <para>The stream order is:</para>
/// <list type="number">
/// <item>Chunk 0: the splats of level 0, the triangles of driver level 0, and the motion of driver level 0 at T0.</item>
/// <item>The motion of driver level 0 at T1, then T2, then T3.</item>
/// <item>For each level <c>k</c> from 1: the triangles of driver level <c>k</c> and the motion of its new vertices at T0,
/// then the splats of level <c>k</c>, then the motion of the new vertices at T1, T2, and T3.</item>
/// </list>
/// <para>Each item is a unit that starts a new chunk. A unit that does not fit in one chunk continues in the next chunks.
/// Splat sections split at parent boundaries, motion sections at frame ranges, and triangle sections at triangle
/// ranges. The output is the same for the same input.</para>
/// <para>Each entry of the chunk table gives the level <c>k</c> of its unit as both the splat level and the driver
/// level, because splat level <c>k</c> binds to driver level <c>k</c>. A cache without driver levels gives driver level 0.</para>
/// <para>The index codec can turn the corners of a triangle. The builder decodes each triangle section as the runtime
/// does, and moves each binding to the decoded triangle, so that the bindings and the stored triangles agree.</para>
/// </remarks>
public static partial class GaussianCacheBuilder
{
    /// <summary>The largest number of data bytes in one chunk. It leaves space for the asset envelope in a 4 MiB payload.</summary>
    public const int MaxChunkDataBytes = 4_128_768;

    /// <summary>The meshoptimizer vertex codec version of every encoded stream.</summary>
    public const ushort MotionCodecVersion = 1;

    /// <summary>
    /// The limit name of a chunk 0 that is too large. The budget message starts with this name and a colon, for example
    /// <c>chunk0: 300000 &gt; 262144</c>.
    /// </summary>
    public const string Chunk0Limit = "chunk0";

    /// <summary>The smallest chunk data limit that a caller can ask for.</summary>
    private const int MinChunkDataBytes = 4_096;

    /// <summary>Builds the cache.</summary>
    /// <exception cref="ArgumentException">The parts of the input do not agree.</exception>
    /// <exception cref="GaussianBakeException">
    /// The input or the output goes above a budget limit (<see cref="GaussianBakeFailureCodes.BudgetExceeded"/>). The message
    /// is <c>&lt;limit&gt;: &lt;actual&gt; &gt; &lt;max&gt;</c>, and the limit is <c>splats</c>, <c>driver-vertices</c>,
    /// <c>duration</c>, <see cref="Chunk0Limit"/>, or <c>cache-bytes</c>.
    /// </exception>
    public static GaussianCacheBuildResult Build(GaussianCacheBuildInput input, GaussianBakeBudget budget, IMeshOptimizerBackend meshopt)
        => Build(input, budget, meshopt, MaxChunkDataBytes);

    /// <summary>Builds the cache with a chunk data limit that is smaller than <see cref="MaxChunkDataBytes"/>.</summary>
    internal static GaussianCacheBuildResult Build(GaussianCacheBuildInput input, GaussianBakeBudget budget, IMeshOptimizerBackend meshopt, int maxChunkDataBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(meshopt);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkDataBytes, MinChunkDataBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxChunkDataBytes, MaxChunkDataBytes);

        int[] staticCounts = Validate(input);
        CheckInputBudgets(input, budget);

        GridQuantizer quantizer = new(input.Bounds, input.GridBits);
        bool animated = input.Drivers.LevelCount > 0;
        int levelCount = input.Tree.LevelCount;
        uint[][] storedTriangles = new uint[input.Drivers.LevelCount][];
        ChunkPlanner planner = new(maxChunkDataBytes);

        AppendRoot(planner, input, staticCounts, quantizer, storedTriangles, budget, meshopt);
        if (animated)
        {
            for (byte temporal = 1; temporal < TemporalLevels.Count; temporal++)
            {
                planner.BeginUnit(GaussianCacheChunkKind.DriverTemporal, 0, 0, temporal);
                AppendMotion(planner, input.Frames, 0, temporal, meshopt);
            }
        }

        for (int level = 1; level < levelCount; level++)
        {
            int driverLevel = animated ? level : 0;
            if (animated)
            {
                planner.BeginUnit(GaussianCacheChunkKind.DriverLevel, level, driverLevel, 0);
                storedTriangles[level] = AppendDriverIndices(planner, input.Drivers, level, meshopt);
                AppendMotion(planner, input.Frames, level, 0, meshopt);
            }

            planner.BeginUnit(GaussianCacheChunkKind.SplatLevel, level, driverLevel, 0);
            FilteredSplatLevel splats = FilterSplatLevel(input, level, staticCounts[level], storedTriangles, quantizer, meshopt);
            AppendSplatLevel(planner, splats, input.Tree.ChildCounts[level - 1], meshopt);

            if (animated)
            {
                for (byte temporal = 1; temporal < TemporalLevels.Count; temporal++)
                {
                    planner.BeginUnit(GaussianCacheChunkKind.DriverTemporal, level, driverLevel, temporal);
                    AppendMotion(planner, input.Frames, level, temporal, meshopt);
                }
            }
        }

        return Assemble(input, staticCounts, planner.Finish(), budget);
    }

    /// <summary>Writes chunk 0. Chunk 0 does not split, so it fails the budget when its sections are too large.</summary>
    private static void AppendRoot(
        ChunkPlanner planner,
        GaussianCacheBuildInput input,
        int[] staticCounts,
        GridQuantizer quantizer,
        uint[][] storedTriangles,
        GaussianBakeBudget budget,
        IMeshOptimizerBackend meshopt)
    {
        List<GaussianCacheSection> sections = [];
        GaussianCacheSection? driverIndices = null;
        if (input.Drivers.LevelCount > 0)
            driverIndices = EncodeWholeDriverLevel(input.Drivers, 0, meshopt, out storedTriangles[0]);

        FilteredSplatLevel splats = FilterSplatLevel(input, 0, staticCounts[0], storedTriangles, quantizer, meshopt);
        sections.Add(EncodeSplatSection(splats, 0, splats.Count, meshopt));
        if (driverIndices is { } indices)
        {
            sections.Add(indices);
            DriverFrameStore frames = input.Frames;
            int frameCount = TemporalLevels.FramesInLevel(0, frames.FrameCount);
            if (frames.GetLevelVertexCount(0) > 0)
                sections.Add(EncodeMotion(frames, 0, 0, 0, frameCount, meshopt));
        }

        long dataBytes = sections.Sum(section => (long)GaussianCacheSection.HeaderSize + section.Payload.Length);
        long limit = Math.Min(budget.Chunk0Bytes, planner.MaxChunkDataBytes);
        if (dataBytes > limit)
            throw Exceeded(Chunk0Limit, dataBytes, limit);

        planner.BeginUnit(GaussianCacheChunkKind.Root, 0, 0, 0);
        foreach (GaussianCacheSection section in sections)
            planner.Append(section);
    }

    /// <summary>Makes the header, checks the output budgets, and makes the assets and the content hash.</summary>
    private static GaussianCacheBuildResult Assemble(
        GaussianCacheBuildInput input,
        int[] staticCounts,
        IReadOnlyList<(GaussianCacheChunkInfo Info, byte[] Data)> chunks,
        GaussianBakeBudget budget)
    {
        GaussianCacheHeader header = CreateHeader(input, staticCounts, chunks);
        byte[] headerBytes = SerializeHeader(header);
        byte[] rootData = chunks[0].Data;

        int chunk0Bytes = checked(headerBytes.Length + rootData.Length);
        if (chunk0Bytes > budget.Chunk0Bytes)
            throw Exceeded(Chunk0Limit, chunk0Bytes, budget.Chunk0Bytes);

        long totalBytes = headerBytes.Length;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(headerBytes);
        foreach ((_, byte[] data) in chunks)
        {
            totalBytes += data.Length;
            hash.AppendData(data);
        }

        if (totalBytes > budget.MaxCacheBytes)
            throw Exceeded("cache-bytes", totalBytes, budget.MaxCacheBytes);

        string[] paths = new string[chunks.Count - 1];
        GaussianCacheChunk[] assets = new GaussianCacheChunk[chunks.Count - 1];
        for (int index = 1; index < chunks.Count; index++)
        {
            paths[index - 1] = ChunkAssetPath(index);
            assets[index - 1] = new GaussianCacheChunk { Index = index, Data = chunks[index].Data };
        }

        GaussianCache root = new()
        {
            Header = header,
            RootChunkData = rootData,
            ChunkAssetPaths = paths,
        };
        return new GaussianCacheBuildResult(root, assets, Convert.ToHexStringLower(hash.GetHashAndReset()), totalBytes, chunk0Bytes);
    }

    /// <summary>Returns the asset path of a chunk, relative to the cache folder.</summary>
    private static string ChunkAssetPath(int index)
        => string.Create(CultureInfo.InvariantCulture, $"chunk_{index:D4}.asset");

    private static GaussianCacheHeader CreateHeader(GaussianCacheBuildInput input, int[] staticCounts, IReadOnlyList<(GaussianCacheChunkInfo Info, byte[] Data)> chunks)
        => new()
        {
            FormatVersion = GaussianCacheHeader.CurrentFormatVersion,
            Representation = GaussianCacheRepresentation.BakedDriver,
            MotionCodecVersion = MotionCodecVersion,
            SplatLevelCounts = [.. input.Tree.Levels.Select(level => level.Length)],
            StaticSplatCounts = [.. staticCounts],
            DriverVertexCounts = [.. input.Drivers.CumulativeVertexCounts],
            DriverTriangleCounts = [.. input.Drivers.LevelTriangleIndices.Select(indices => indices.Length / 3)],
            FrameCount = input.Frames.FrameCount,
            FrameRate = input.Motion.FrameRate,
            DurationSeconds = input.Motion.DurationSeconds,
            LoopMode = input.LoopMode,
            Bounds = input.Bounds,
            GridBits = input.GridBits,
            ColorScale = input.ColorScale,
            CameraHints = CopyHints(input.CameraHints),
            Chunks = [.. chunks.Select(chunk => chunk.Info)],
        };

    private static GaussianCacheCameraHints? CopyHints(GaussianCacheCameraHints? hints)
        => hints is null
            ? null
            : new GaussianCacheCameraHints
            {
                OrbitTarget = hints.OrbitTarget,
                MinRadius = hints.MinRadius,
                MaxRadius = hints.MaxRadius,
                DefaultRadius = hints.DefaultRadius,
                DefaultYawDegrees = hints.DefaultYawDegrees,
                DefaultPitchDegrees = hints.DefaultPitchDegrees,
                Path = [.. hints.Path],
            };

    private static byte[] SerializeHeader(GaussianCacheHeader header)
    {
        using MemoryStream stream = new(checked((int)header.CalculateByteSize()));
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            header.Write(writer);
        return stream.ToArray();
    }

    private static void CheckInputBudgets(GaussianCacheBuildInput input, GaussianBakeBudget budget)
    {
        int splats = input.Tree.Levels[^1].Length;
        if (splats > budget.MaxSplats)
            throw Exceeded("splats", splats, budget.MaxSplats);

        int driverVertices = input.Drivers.VertexCount;
        if (driverVertices > budget.MaxDriverVertices)
            throw Exceeded("driver-vertices", driverVertices, budget.MaxDriverVertices);

        float duration = input.Motion.DurationSeconds;
        if (duration > budget.MaxDurationSeconds)
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.BudgetExceeded,
                string.Create(CultureInfo.InvariantCulture, $"duration: {duration} > {budget.MaxDurationSeconds}"));
    }

    private static GaussianBakeException Exceeded(string limit, long actual, long max)
        => new(GaussianBakeFailureCodes.BudgetExceeded, string.Create(CultureInfo.InvariantCulture, $"{limit}: {actual} > {max}"));
}
