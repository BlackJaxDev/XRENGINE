using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Geometry;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Output;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Gaussian.Baking.Pipeline;

/// <summary>
/// Runs a gaussian preview bake from start to end: it turns a cooked prefab or world into a progressive
/// <see cref="GaussianCache"/>, the loop videos, the posters, and <c>report.json</c>.
/// </summary>
/// <remarks>
/// <para>The steps are, in this order:</para>
/// <list type="number">
/// <item>Check the options and the ffmpeg encoders. Nothing is loaded or rendered before these checks.</item>
/// <item>Load the input and check that it has a renderable mesh.</item>
/// <item>Choose the motion, and sample all frames: the largest distance that each vertex moves and the bounds over
/// all frames. These give the grid bits and the grid step. Then break each seam weld that opens by more than one grid
/// step in a frame, and mark the meshes that move more than one grid step as animated.</item>
/// <item>Place the splats on the rest surface (time 0). A splat on a mesh that is not animated is static.</item>
/// <item>Bake the splat colors from engine renders of the content posed at time 0.</item>
/// <item>Build the splat level tree, the driver meshes, the quantized driver frames, and the splat bindings. Choose the
/// loop mode, and build the cache. When chunk 0 is too large, divide the level 0 target by 8 and build again, at most
/// two times.</item>
/// <item>Write the cache files, record the loop videos and posters, write <c>report.json</c>, and commit the output
/// folder.</item>
/// </list>
/// <para>All outputs go to a staging folder first (see <see cref="BakeOutputDirectory"/>). A failed bake leaves only
/// <c>report.json</c> in the output folder. The same input, engine version, options, machine, and GPU driver give the
/// same output files, except <c>report.json</c>, which has timings.</para>
/// </remarks>
public sealed partial class GaussianBakePipeline
{
    /// <summary>The number of capture views around the content.</summary>
    public const int OrbitViewCount = 32;

    /// <summary>The number of extra capture views of a world, from the center of its bounds.</summary>
    public const int InteriorViewCount = 32;

    /// <summary>The width of each capture view, in pixels.</summary>
    public const int ViewWidth = 1920;

    /// <summary>The height of each capture view, in pixels.</summary>
    public const int ViewHeight = 1080;

    /// <summary>The warning when no capture view saw a splat, so that every splat color is a fill color.</summary>
    public const string NoLeafSeenWarning = "no-leaf-seen";

    /// <summary>
    /// The warning when the chosen motion moves no mesh by more than one grid step, or when no animated triangle can
    /// drive splats. The cache is then one still frame.
    /// </summary>
    public const string MotionStaticWarning = "clip-static";

    /// <summary>The warning when chunk 0 is too large and the bake builds again with a smaller level 0 target. The target follows it.</summary>
    public const string Chunk0RetryWarning = "chunk0-retry:";

    /// <summary>The longest time to wait for the content thread when the bake puts back a pose or releases a player.</summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    private readonly IBakeRenderServices _render;
    private readonly IMeshOptimizerBackend _meshopt;
    private readonly Func<FfmpegEncoder, CancellationToken, Task<IReadOnlyList<string>>> _verifyEncoders;

    /// <summary>Creates a pipeline.</summary>
    /// <param name="render">The renderer that captures the views and records the loop.</param>
    /// <param name="meshopt">The mesh optimizer.</param>
    public GaussianBakePipeline(IBakeRenderServices render, IMeshOptimizerBackend meshopt)
        : this(render, meshopt, static (encoder, cancellationToken) => encoder.VerifyEncodersAsync(cancellationToken))
    {
    }

    /// <summary>Creates a pipeline with another encoder check, for tests.</summary>
    /// <param name="render">The renderer that captures the views and records the loop.</param>
    /// <param name="meshopt">The mesh optimizer.</param>
    /// <param name="verifyEncoders">Returns the names of the encoders that ffmpeg does not have.</param>
    internal GaussianBakePipeline(
        IBakeRenderServices render,
        IMeshOptimizerBackend meshopt,
        Func<FfmpegEncoder, CancellationToken, Task<IReadOnlyList<string>>> verifyEncoders)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _meshopt = meshopt ?? throw new ArgumentNullException(nameof(meshopt));
        _verifyEncoders = verifyEncoders ?? throw new ArgumentNullException(nameof(verifyEncoders));
    }

    /// <summary>
    /// Makes a report for a bake that has not started: the versions, the kind, and the input paths.
    /// </summary>
    /// <param name="options">The bake options.</param>
    /// <param name="meshopt">The mesh optimizer whose version the report gives.</param>
    public static BakeReport CreateReport(BakeSplatPreviewOptions options, IMeshOptimizerBackend meshopt)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(meshopt);

        return new BakeReport
        {
            EngineVersion = EngineVersion(),
            MeshoptimizerVersion = MeshoptimizerVersion(meshopt.VersionKey),
            Kind = KindName(options.Kind),
            Input = new BakeReportInput { Pak = options.InputPak, Asset = options.AssetPath },
        };
    }

    /// <summary>
    /// Runs the bake and writes its outputs to <see cref="BakeSplatPreviewOptions.OutputDirectory"/>.
    /// </summary>
    /// <param name="options">The bake options.</param>
    /// <param name="cancellationToken">Stops the bake.</param>
    /// <returns>
    /// The report. Its status is <see cref="BakeReport.ReadyStatus"/>, or <see cref="BakeReport.FailedStatus"/> with a
    /// failure code. The output folder holds the same report.
    /// </returns>
    /// <exception cref="Exception">
    /// An error that has no failure code. The output folder then holds a report with the failure code
    /// <see cref="GaussianBakeFailureCodes.Unexpected"/>, and the exception goes to the caller with its stack trace.
    /// </exception>
    public async Task<BakeReport> RunAsync(BakeSplatPreviewOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        BakeReport report = CreateReport(options, _meshopt);
        BakeOutputDirectory output;
        try
        {
            output = BakeOutputDirectory.Begin(options.OutputDirectory);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            report.MarkFailed(GaussianBakeFailureCodes.EncodeFailed, $"The output folder '{options.OutputDirectory}' cannot be prepared: {ex.Message}");
            return report;
        }

        BakeRun run = new(options, report, output.StagingPath);
        try
        {
            await RunStagesAsync(run, cancellationToken).ConfigureAwait(false);
            report.MarkReady();
            BakeReportWriter.Write(report, output.StagingPath);
            output.Commit();
            return report;
        }
        catch (GaussianBakeException ex)
        {
            report.MarkFailed(ex.FailureCode, ex.Message);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            report.MarkFailed(GaussianBakeFailureCodes.EncodeFailed, $"The outputs cannot be written to '{options.OutputDirectory}': {ex.Message}");
        }
        catch (Exception ex)
        {
            string detail = ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? "The bake stopped before it was complete."
                : $"{ex.GetType().FullName}: {ex.Message}";
            report.MarkFailed(GaussianBakeFailureCodes.Unexpected, detail);
            TryWriteFailure(output, report);
            throw;
        }
        finally
        {
            await ReleaseTemporaryPlayersAsync(run).ConfigureAwait(false);
        }

        TryWriteFailure(output, report);
        return report;
    }

    /// <summary>Runs every step of the bake. Each step adds its time to the report.</summary>
    private async Task RunStagesAsync(BakeRun run, CancellationToken cancellationToken)
    {
        BakeSplatPreviewOptions options = run.Options;
        BakeReport report = run.Report;
        GaussianBakeBudget budget = GaussianBakeBudget.For(options.Kind);
        Stopwatch watch = Stopwatch.StartNew();

        FfmpegEncoder encoder = await CheckOptionsAndEncodersAsync(options, cancellationToken).ConfigureAwait(false);
        CookedBakeInput input = CookedBakeInputLoader.Load(options.InputPak, options.AssetPath, options.Kind);
        run.Input = input;
        report.Input!.Sha256 = input.InputSha256;
        run.Roots = ContentRoots(input);
        if (!HasRenderableMesh(run.Roots))
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.ContentEmpty,
                $"The asset '{options.AssetPath}' has no active renderable component with a mesh.");
        }

        report.TimingsMs.Load = Lap(watch);

        ContentAnalysis analysis = await AnalyzeAsync(run, budget, cancellationToken).ConfigureAwait(false);
        GridQuantizer quantizer = new(analysis.Bounds, analysis.GridBits);
        report.TimingsMs.Animate += Lap(watch);

        int splatCount = options.SplatCount ?? budget.MaxSplats;
        BakeRandom random = new(BakeRandom.SeedFrom(input.InputSha256, options.Kind));
        BakedSplat[] leaves = SurfaceSampler.Sample(analysis.Rest, splatCount, random, _meshopt);
        if (leaves.Length == 0)
            throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The asset '{options.AssetPath}' has no triangle with an area that can receive splats.");
        MarkStaticSplats(leaves, analysis.Rest, analysis.AnimatedMeshes);
        report.TimingsMs.Sample = Lap(watch);

        float colorScale = await BakeColorsAsync(run, analysis, leaves, cancellationToken).ConfigureAwait(false);
        report.TimingsMs.Color = Lap(watch);

        CacheBuild build = await BuildCacheAsync(run, analysis, leaves, quantizer, colorScale, budget, cancellationToken).ConfigureAwait(false);
        Stopwatch encodeWatch = Stopwatch.StartNew();
        BakeCacheFiles.Write(build.Result, run.StagingPath);
        report.TimingsMs.Encode += encodeWatch.ElapsedMilliseconds;
        FillCacheReport(report, build);
        watch.Restart();

        GaussianCacheHeader header = build.Result.Root.Header;
        LoopTiming timing = LoopCameraPlanner.ChooseTiming(options.Kind, header);
        BakeCameraPose[] poses = LoopCameraPlanner.Plan(header.CameraHints!, timing.DurationSeconds, timing.FrameCount);
        await _render.RecordLoopAsync(input, build.Result, poses, timing.FramesPerSecond, encoder, run.StagingPath, cancellationToken).ConfigureAwait(false);
        FillOutputsReport(report, run.StagingPath);
        report.TimingsMs.Loop = Lap(watch);
    }

    /// <summary>
    /// Fails <c>optimized-fit</c> with <see cref="GaussianBakeFailureCodes.UnsupportedOption"/>, and fails a missing ffmpeg
    /// encoder with <see cref="GaussianBakeFailureCodes.EncodeFailed"/>. Returns the encoder.
    /// </summary>
    private async Task<FfmpegEncoder> CheckOptionsAndEncodersAsync(BakeSplatPreviewOptions options, CancellationToken cancellationToken)
    {
        if (string.Equals(options.ColorMode, BakeSplatPreviewOptions.OptimizedFitColorMode, StringComparison.Ordinal))
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.UnsupportedOption,
                $"The color mode {BakeSplatPreviewOptions.OptimizedFitColorMode} is not supported by this engine version.");
        }

        FfmpegEncoder encoder = new(options.FfmpegPath, options.H264Encoder);
        IReadOnlyList<string> missing = await _verifyEncoders(encoder, cancellationToken).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.EncodeFailed,
                $"The ffmpeg executable '{options.FfmpegPath}' does not have the encoders {string.Join(", ", missing)}. " +
                "Give an ffmpeg build with these encoders with --ffmpeg or XRE_FFMPEG_PATH.");
        }

        return encoder;
    }

    /// <summary>
    /// Returns the root nodes of the content: the prefab instance, or every root node of every world scene. The list is
    /// made before the render world exists, so a scene that the bake adds later (such as a camera rig) is not content.
    /// </summary>
    private static SceneNode[] ContentRoots(CookedBakeInput input)
    {
        if (input.PrefabRoot is SceneNode prefabRoot)
            return [prefabRoot];

        List<SceneNode> roots = [];
        if (input.World is XRWorld world)
        {
            foreach (XRScene? scene in world.Scenes)
            {
                if (scene is null)
                    continue;

                foreach (SceneNode? root in scene.RootNodes)
                {
                    if (root is not null)
                        roots.Add(root);
                }
            }
        }

        return [.. roots];
    }

    /// <summary>
    /// Puts back the content after the bake: destroys the temporary players of the motion choice on the content thread.
    /// The wait is limited, because the engine can stop before the work runs.
    /// </summary>
    private async Task ReleaseTemporaryPlayersAsync(BakeRun run)
    {
        if (run.Input is not CookedBakeInput input || run.Choice is not { TemporaryPlayers.Count: > 0 } choice)
            return;

        await RunReleaseAsync(input, () => BakeMotionSelector.ReleaseTemporaryPlayers(choice)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs release work on the content thread and waits at most <see cref="ReleaseTimeout"/>. A timeout or a stop of the
    /// engine is not an error here: the content is not used after the bake.
    /// </summary>
    private async Task RunReleaseAsync(CookedBakeInput input, Action work)
    {
        try
        {
            await _render.RunOnContentThreadAsync(
                input,
                () =>
                {
                    work();
                    return true;
                },
                CancellationToken.None).WaitAsync(ReleaseTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private static void TryWriteFailure(BakeOutputDirectory output, BakeReport report)
    {
        try
        {
            output.Fail(report);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            // The report stays available to the caller, which prints it.
        }
    }

    private static bool IsFileError(Exception ex)
        => ex is IOException or UnauthorizedAccessException;

    private static long Lap(Stopwatch watch)
    {
        long elapsed = watch.ElapsedMilliseconds;
        watch.Restart();
        return elapsed;
    }

    private static string KindName(GaussianBakeKind kind)
        => kind switch
        {
            GaussianBakeKind.Avatar => "avatar",
            GaussianBakeKind.Item => "item",
            GaussianBakeKind.World => "world",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bake kind is not defined."),
        };

    /// <summary>Returns the informational version of the engine rendering assembly.</summary>
    private static string EngineVersion()
    {
        Assembly assembly = typeof(GaussianCache).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    /// <summary>
    /// Returns the meshoptimizer version from the version key of the backend: the text before the first <c>;</c>, for
    /// example <c>v1.3 9e1f07b159d3cb777f1c67ed31fc11fd117986f4</c>.
    /// </summary>
    private static string MeshoptimizerVersion(string? versionKey)
    {
        if (string.IsNullOrWhiteSpace(versionKey))
            return "unknown";

        int separator = versionKey.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? versionKey : versionKey[..separator]).Trim();
    }

    /// <summary>Returns the bounds of a list of points given as x, y, and z.</summary>
    private static AABB BoundsOf(float[] positions)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            Vector3 point = new(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        return new AABB(min, max);
    }
}
