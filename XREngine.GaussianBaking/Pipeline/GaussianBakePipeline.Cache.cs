using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Lod;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Pipeline;

// The steps that build the cache: the splat tree, the drivers, the frames, the bindings, and the cache assets.
public sealed partial class GaussianBakePipeline
{
    /// <summary>The largest number of builds again after chunk 0 is too large.</summary>
    private const int MaxChunk0Retries = 2;

    /// <summary>A build again divides the level 0 target by this value.</summary>
    private const int Chunk0RetryDivisor = 8;

    /// <summary>The start of the budget message of a chunk 0 that is too large.</summary>
    private const string Chunk0LimitPrefix = "chunk0:";

    /// <summary>The camera framing of the cache hints and the orbit views: the bounding sphere fills a 40 degree field of view with a 10 percent margin.</summary>
    private const float HalfFieldOfViewDegrees = 20.0f;
    private const float FramingMargin = 1.1f;
    private const float MinRadiusFactor = 0.5f;
    private const float MaxRadiusFactor = 3.0f;
    private const float DefaultPitchDegrees = 15.0f;

    /// <summary>The smallest default camera distance, in meters, for content with bounds of almost no size.</summary>
    private const float MinimumDefaultRadius = 0.01f;

    /// <summary>
    /// Builds the splat tree, the drivers, the frames, and the bindings, and then the cache. When the cache builder fails
    /// because chunk 0 is too large, the level 0 target becomes 8 times smaller and everything after the leaves builds
    /// again, at most <see cref="MaxChunk0Retries"/> times. The driver trajectories are sampled one time, because each
    /// build uses driver vertices from the same top driver level.
    /// </summary>
    private async Task<CacheBuild> BuildCacheAsync(
        BakeRun run,
        ContentAnalysis analysis,
        BakedSplat[] leaves,
        GridQuantizer quantizer,
        float colorScale,
        GaussianBakeBudget budget,
        CancellationToken cancellationToken)
    {
        BakeReportTimings timings = run.Report.TimingsMs;
        GaussianCacheCameraHints hints = CreateCameraHints(analysis.Bounds, analysis.CameraPath);
        int l0Target = budget.L0TargetSplats;
        int retries = 0;
        DriverTrajectories? trajectories = null;
        while (true)
        {
            Stopwatch watch = Stopwatch.StartNew();
            SplatLodTree tree = SplatLodTreeBuilder.Build(leaves, l0Target, _meshopt);
            timings.Tree += Lap(watch);

            DriverHierarchy drivers = HasAnimatedSplat(leaves)
                ? DriverHierarchyBuilder.Build(analysis.Rest, analysis.AnimatedMeshes, analysis.Weld, LevelCounts(tree), budget, _meshopt)
                : DriverHierarchy.Empty;
            if (drivers.LevelCount > 0 && drivers.CumulativeVertexCounts[^1] > budget.MaxDriverVertices)
            {
                throw new GaussianBakeException(
                    GaussianBakeFailureCodes.BudgetExceeded,
                    string.Create(CultureInfo.InvariantCulture, $"driver-vertices: {drivers.CumulativeVertexCounts[^1]} > {budget.MaxDriverVertices}"));
            }

            if (drivers.LevelCount == 0 && HasAnimatedSplat(leaves))
            {
                // No animated triangle can drive splats, so every splat stays at its rest position.
                for (int i = 0; i < leaves.Length; i++)
                    leaves[i].IsStatic = true;
                timings.Driver += Lap(watch);
                continue;
            }

            timings.Driver += Lap(watch);
            GaussianCacheBuildInput input;
            if (drivers.LevelCount == 0)
            {
                input = CreateStillInput(run, analysis, tree, colorScale, hints, budget);
            }
            else
            {
                if (trajectories is null || !trajectories.Covers(drivers.SourceVertexOfDriverVertex))
                {
                    int[] sources = drivers.SourceVertexOfDriverVertex;
                    SceneNode[] roots = run.Roots;
                    BakeMotionChoice choice = analysis.Choice;
                    DeformedSurface rest = analysis.Rest;
                    trajectories = await _render.RunOnContentThreadAsync(
                        run.Input!,
                        () => SampleTrajectories(roots, choice, rest, sources),
                        cancellationToken).ConfigureAwait(false);
                    timings.Animate += Lap(watch);
                }

                input = CreateAnimatedInput(run, analysis, tree, drivers, trajectories, quantizer, colorScale, hints, budget);
                timings.Driver += Lap(watch);
            }

            try
            {
                GaussianCacheBuildResult result = GaussianCacheBuilder.Build(input, budget, _meshopt);
                timings.Encode += Lap(watch);
                return new CacheBuild(result, input.Motion);
            }
            catch (GaussianBakeException ex) when (
                ex.FailureCode == GaussianBakeFailureCodes.BudgetExceeded
                && ex.Message.StartsWith(Chunk0LimitPrefix, StringComparison.Ordinal)
                && retries < MaxChunk0Retries)
            {
                timings.Encode += Lap(watch);
                retries++;
                l0Target = Math.Max(1, l0Target / Chunk0RetryDivisor);
                run.Report.Warnings.Add(Chunk0RetryWarning + l0Target.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// Makes the input of a still cache: every splat is static, there is no driver and no motion, the cache has one
    /// frame, and its loop mode is <see cref="GaussianCacheLoopMode.Once"/>. A choice with frames gets the warning
    /// <see cref="MotionStaticWarning"/>.
    /// </summary>
    private static GaussianCacheBuildInput CreateStillInput(
        BakeRun run,
        ContentAnalysis analysis,
        SplatLodTree tree,
        float colorScale,
        GaussianCacheCameraHints hints,
        GaussianBakeBudget budget)
    {
        BakeMotionChoice choice = analysis.Choice;
        if (choice.FrameCount > 1 && !run.Report.Warnings.Contains(MotionStaticWarning))
            run.Report.Warnings.Add(MotionStaticWarning);

        BakeMotionChoice still = new([], choice.Source, choice.ClipName, 0.0f, 1, budget.FrameRate);
        SplatBinding[][] bindings = new SplatBinding[tree.LevelCount][];
        for (int level = 0; level < bindings.Length; level++)
            bindings[level] = [];

        return new GaussianCacheBuildInput
        {
            Kind = run.Options.Kind,
            Tree = tree,
            Drivers = DriverHierarchy.Empty,
            BindingsPerLevel = bindings,
            DriverRestPositions = [],
            Frames = new DriverFrameStore([], 1, GaussianCacheLoopMode.Once),
            Motion = still,
            LoopMode = GaussianCacheLoopMode.Once,
            Bounds = analysis.Bounds,
            GridBits = analysis.GridBits,
            ColorScale = colorScale,
            CameraHints = hints,
        };
    }

    /// <summary>
    /// Makes the input of an animated cache. The loop mode is <see cref="GaussianCacheLoopMode.PingPong"/> when a driver
    /// vertex is more than two times the largest position error from its start position at the end of the motion, or
    /// else <see cref="GaussianCacheLoopMode.Loop"/>. The bindings use the same driver rest positions as the cache.
    /// </summary>
    private static GaussianCacheBuildInput CreateAnimatedInput(
        BakeRun run,
        ContentAnalysis analysis,
        SplatLodTree tree,
        DriverHierarchy drivers,
        DriverTrajectories trajectories,
        GridQuantizer quantizer,
        float colorScale,
        GaussianCacheCameraHints hints,
        GaussianBakeBudget budget)
    {
        GaussianCacheLoopMode loopMode = trajectories.MaxEndDistance > 2.0f * budget.MaxPositionErrorMeters
            ? GaussianCacheLoopMode.PingPong
            : GaussianCacheLoopMode.Loop;
        DriverFrameStore frames = QuantizeFrames(drivers, trajectories, quantizer, loopMode);

        Vector3[] driverRest = new Vector3[drivers.VertexCount];
        drivers.GatherPositions(analysis.Rest, driverRest);
        SplatBinding[][] bindings = new SplatBinding[tree.LevelCount][];
        for (int level = 0; level < tree.LevelCount; level++)
        {
            BakedSplat[] splats = tree.Levels[level];
            int staticCount = 0;
            while (staticCount < splats.Length && splats[staticCount].IsStatic)
                staticCount++;
            bindings[level] = SplatBinder.Bind(splats.AsSpan(staticCount), drivers, level, driverRest);
        }

        return new GaussianCacheBuildInput
        {
            Kind = run.Options.Kind,
            Tree = tree,
            Drivers = drivers,
            BindingsPerLevel = bindings,
            DriverRestPositions = driverRest,
            Frames = frames,
            Motion = analysis.Choice,
            LoopMode = loopMode,
            Bounds = analysis.Bounds,
            GridBits = analysis.GridBits,
            ColorScale = colorScale,
            CameraHints = hints,
        };
    }

    /// <summary>Quantizes the position of each driver vertex in each frame onto the grid of the cache.</summary>
    private static DriverFrameStore QuantizeFrames(DriverHierarchy drivers, DriverTrajectories trajectories, GridQuantizer quantizer, GaussianCacheLoopMode loopMode)
    {
        DriverFrameStore frames = new(drivers.CumulativeVertexCounts, trajectories.FrameCount, loopMode);
        int[] sources = drivers.SourceVertexOfDriverVertex;
        ushort[] positions = frames.Positions;
        for (int frame = 0; frame < trajectories.FrameCount; frame++)
        {
            for (int driver = 0; driver < sources.Length; driver++)
            {
                quantizer.Quantize(trajectories.PositionOf(sources[driver], frame), out ushort x, out ushort y, out ushort z);
                int offset = ((frame * sources.Length) + driver) * 3;
                positions[offset] = x;
                positions[offset + 1] = y;
                positions[offset + 2] = z;
            }
        }

        return frames;
    }

    /// <summary>
    /// Makes the camera hints: the orbit target is the center of the bounds over all frames; the default distance fits
    /// the bounding sphere in a 40 degree field of view with a 10 percent margin; the distance range is 0.5 to 3 times
    /// the default; the default yaw is 0 and the default pitch is 15 degrees; the path is the camera path of the content.
    /// </summary>
    private static GaussianCacheCameraHints CreateCameraHints(AABB bounds, GaussianCacheCameraKey[] path)
    {
        float sphereRadius = bounds.Size.Length() * 0.5f;
        float defaultRadius = sphereRadius / MathF.Sin(HalfFieldOfViewDegrees * MathF.PI / 180.0f) * FramingMargin;
        if (!(defaultRadius >= MinimumDefaultRadius))
            defaultRadius = MinimumDefaultRadius;

        return new GaussianCacheCameraHints
        {
            OrbitTarget = bounds.Center,
            DefaultRadius = defaultRadius,
            MinRadius = MinRadiusFactor * defaultRadius,
            MaxRadius = MaxRadiusFactor * defaultRadius,
            DefaultYawDegrees = 0.0f,
            DefaultPitchDegrees = DefaultPitchDegrees,
            Path = path,
        };
    }

    private static int[] LevelCounts(SplatLodTree tree)
    {
        int[] counts = new int[tree.LevelCount];
        for (int level = 0; level < counts.Length; level++)
            counts[level] = tree.Levels[level].Length;
        return counts;
    }

    private static bool HasAnimatedSplat(BakedSplat[] leaves)
    {
        foreach (BakedSplat leaf in leaves)
        {
            if (!leaf.IsStatic)
                return true;
        }

        return false;
    }

    /// <summary>Writes the motion, splat, driver, frame, grid, color, and cache sections of the report from the built cache.</summary>
    private static void FillCacheReport(BakeReport report, CacheBuild build)
    {
        GaussianCacheBuildResult result = build.Result;
        GaussianCacheHeader header = result.Root.Header;
        report.Clip = new BakeReportClip
        {
            Name = build.Motion.ClipName,
            Source = build.Motion.Source,
            DurationSeconds = header.DurationSeconds,
            LoopMode = header.LoopMode.ToString(),
        };
        report.Splats = new BakeReportSplats
        {
            Total = header.SplatLevelCounts[^1],
            Static = header.StaticSplatCounts[^1],
            PerLevel = [.. header.SplatLevelCounts],
        };
        report.Driver = new BakeReportDriver
        {
            VerticesPerLevel = [.. header.DriverVertexCounts],
            TrianglesPerLevel = [.. header.DriverTriangleCounts],
        };
        report.Frames = new BakeReportFrames { Count = header.FrameCount, Rate = header.FrameRate };
        report.GridBits = header.GridBits;
        report.ColorScale = header.ColorScale;

        List<BakeReportChunk> chunks = new(header.Chunks.Length);
        foreach (GaussianCacheChunkInfo info in header.Chunks)
        {
            chunks.Add(new BakeReportChunk
            {
                Index = info.Index,
                Kind = info.Kind.ToString(),
                SplatLevel = info.SplatLevel,
                DriverLevel = info.DriverLevel,
                TemporalLevel = info.TemporalLevel,
                Bytes = info.ByteLength,
                Sha256 = info.Sha256,
            });
        }

        report.Cache = new BakeReportCache
        {
            ContentHash = result.ContentHash,
            Chunk0Bytes = result.Chunk0Bytes,
            TotalBytes = result.TotalBytes,
            Chunks = chunks,
        };
    }

    /// <summary>Writes the size of each loop video and poster in the output folder into the report.</summary>
    /// <exception cref="GaussianBakeException">A file is missing (<see cref="GaussianBakeFailureCodes.EncodeFailed"/>).</exception>
    private static void FillOutputsReport(BakeReport report, string directory)
    {
        report.Outputs.Clear();
        foreach (LoopRendition rendition in LoopRendition.All)
        {
            FileInfo file = new(Path.Combine(directory, rendition.FileName));
            if (!file.Exists)
                throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The loop recording did not write '{rendition.FileName}'.");

            report.Outputs.Add(new BakeReportOutput
            {
                File = rendition.FileName,
                Height = rendition.Height,
                Codec = rendition.Codec,
                Bytes = file.Length,
            });
        }
    }

    /// <summary>The state of one bake run.</summary>
    private sealed class BakeRun(BakeSplatPreviewOptions options, BakeReport report, string stagingPath)
    {
        public BakeSplatPreviewOptions Options { get; } = options;

        public BakeReport Report { get; } = report;

        public string StagingPath { get; } = stagingPath;

        /// <summary>Gets or sets the loaded input. It is null until the load.</summary>
        public CookedBakeInput? Input { get; set; }

        /// <summary>Gets or sets the root nodes of the content.</summary>
        public SceneNode[] Roots { get; set; } = [];

        /// <summary>Gets or sets the motion choice. It is null until the first frame pass.</summary>
        public BakeMotionChoice? Choice { get; set; }
    }

    /// <summary>The built cache and the motion that it stores.</summary>
    private sealed record CacheBuild(GaussianCacheBuildResult Result, BakeMotionChoice Motion);
}
