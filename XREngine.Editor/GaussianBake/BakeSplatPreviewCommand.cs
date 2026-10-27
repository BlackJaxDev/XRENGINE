using XREngine.Data.Geometry;
using XREngine.Imaging;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Imaging;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Output;
using XREngine.Rendering.Models.Gaussian.Baking.Pipeline;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Report;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Runs the <c>--bake-splat-preview</c> editor command. The command reads a cooked prefab or world from a
/// <c>.pak</c> archive and bakes it in a hidden engine window with <see cref="GaussianBakePipeline"/>: the gaussian
/// cache, the loop videos, the posters, and <c>report.json</c>. With <c>--dump-views-only</c>, it writes the capture
/// views as PNG files and stops.
/// </summary>
public static class BakeSplatPreviewCommand
{
    /// <summary>The number of capture views around the content.</summary>
    public const int ViewCount = 32;

    /// <summary>The width of each capture view, in pixels.</summary>
    public const int ViewWidth = 1920;

    /// <summary>The height of each capture view, in pixels.</summary>
    public const int ViewHeight = 1080;

    /// <summary>The exit code for an error that has no failure code.</summary>
    private const int UnexpectedExitCode = 1;

    /// <summary>
    /// Runs the command when the arguments contain <see cref="BakeSplatPreviewOptions.CommandFlag"/>, then ends the
    /// process with the bake exit code. Returns false, and does nothing, for all other arguments.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (!ContainsCommandFlag(args))
            return false;

        int exitCode = Run(args);
        Environment.Exit(exitCode);
        return true;
    }

    private static bool ContainsCommandFlag(string[] args)
    {
        foreach (string argument in args)
            if (string.Equals(argument, BakeSplatPreviewOptions.CommandFlag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int Run(string[] args)
    {
        // The options are checked before any file is read.
        if (!BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error) || options is null)
            return Fail(GaussianBakeFailureCodes.InvalidArguments, error ?? "The arguments are not valid.");

        return options.DumpViewsOnly ? RunDumpViews(options) : RunBake(options);
    }

    /// <summary>
    /// Runs the full bake. The option and encoder checks, the input load, and the stage load run before the engine
    /// starts, as the load of a cooked world must complete before the engine runs. The rest of the pipeline then runs in
    /// the hidden engine window. The exit code is 0 for a bake that is ready, or the exit code of the failure code of
    /// the report.
    /// </summary>
    private static int RunBake(BakeSplatPreviewOptions options)
    {
        IMeshOptimizerBackend meshopt;
        try
        {
            meshopt = MeshOptimizerBackendServices.Required;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Gaussian preview bake failed with an unexpected error: {ex.Message}");
            return UnexpectedExitCode;
        }

        GaussianBakePreparation preparation;
        SceneNode stage;
        try
        {
            preparation = GaussianBakePipeline.PrepareAsync(options, meshopt, CancellationToken.None).GetAwaiter().GetResult();
            if (!preparation.IsPrepared)
                return PrintResult(options, preparation.Report);

            stage = ResolveStage(options);
        }
        catch (GaussianBakeException ex)
        {
            // Only the stage load throws here: the preparation reports its own failures.
            BakeReport report = GaussianBakePipeline.CreateReport(options, meshopt);
            report.MarkFailed(ex.FailureCode, ex.Message);
            WriteFailedReport(options, report);
            return Fail(ex.FailureCode, ex.Message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Gaussian preview bake failed with an unexpected error: {ex}");
            return UnexpectedExitCode;
        }

        int exitCode;
        try
        {
            exitCode = BakeRenderHost.Run(
                options,
                stage,
                async (services, cancellationToken) =>
                {
                    BakeReport report = await new GaussianBakePipeline(services, meshopt).RunAsync(preparation, cancellationToken).ConfigureAwait(false);
                    return PrintResult(options, report);
                });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Gaussian preview bake failed with an unexpected engine error: {ex}");
            exitCode = UnexpectedExitCode;
        }

        // A bake that the engine stopped before it started still gets a failed report.
        if (preparation.IsPrepared)
        {
            preparation.Fail(
                exitCode == GaussianBakeFailureCodes.ExitCodeFor(GaussianBakeFailureCodes.CaptureFailed)
                    ? GaussianBakeFailureCodes.CaptureFailed
                    : GaussianBakeFailureCodes.Unexpected,
                "The engine stopped before the bake started.");
        }

        return exitCode;
    }

    /// <summary>Prints the result of the bake and returns its exit code.</summary>
    private static int PrintResult(BakeSplatPreviewOptions options, BakeReport report)
    {
        string output = Path.GetFullPath(options.OutputDirectory);
        if (!string.Equals(report.Status, BakeReport.ReadyStatus, StringComparison.Ordinal))
        {
            Console.WriteLine($"Gaussian preview bake report: {Path.Combine(output, BakeReportWriter.FileName)}");
            return Fail(report.FailureCode ?? GaussianBakeFailureCodes.Unexpected, report.FailureDetail ?? string.Empty);
        }

        BakeReportTimings timings = report.TimingsMs;
        long totalMs = timings.Load + timings.Animate + timings.Sample + timings.Color + timings.Tree + timings.Driver + timings.Encode + timings.Loop;
        Console.WriteLine($"Gaussian preview bake ready: {output}");
        Console.WriteLine($"  splats per level: {Join(report.Splats?.PerLevel)} (static {report.Splats?.Static ?? 0})");
        Console.WriteLine($"  driver vertices per level: {Join(report.Driver?.VerticesPerLevel)}, triangles per level: {Join(report.Driver?.TrianglesPerLevel)}");
        Console.WriteLine($"  frames: {report.Frames?.Count} at {report.Frames?.Rate} fps, loop mode {report.Clip?.LoopMode}, clip '{report.Clip?.Name}' ({report.Clip?.Source})");
        Console.WriteLine($"  chunks: {report.Cache?.Chunks.Count}, chunk 0: {report.Cache?.Chunk0Bytes} bytes, total: {report.Cache?.TotalBytes} bytes, content hash {report.Cache?.ContentHash}");
        Console.WriteLine($"  time: {totalMs} ms (load {timings.Load}, animate {timings.Animate}, sample {timings.Sample}, color {timings.Color}, tree {timings.Tree}, driver {timings.Driver}, encode {timings.Encode}, loop {timings.Loop})");
        if (report.Warnings.Count > 0)
            Console.WriteLine($"  warnings: {string.Join(", ", report.Warnings)}");
        return 0;
    }

    private static string Join(int[]? values)
        => values is null ? "-" : string.Join("/", values);

    /// <summary>Writes a failed report into the output folder. A file error only prints a message.</summary>
    private static void WriteFailedReport(BakeSplatPreviewOptions options, BakeReport report)
    {
        try
        {
            BakeOutputDirectory.Begin(options.OutputDirectory).Fail(report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Gaussian preview bake: the report cannot be written to '{options.OutputDirectory}': {ex.Message}");
        }
    }

    /// <summary>Writes the capture views as PNG files and stops (<c>--dump-views-only</c>).</summary>
    private static int RunDumpViews(BakeSplatPreviewOptions options)
    {
        if (string.Equals(options.ColorMode, BakeSplatPreviewOptions.OptimizedFitColorMode, StringComparison.Ordinal))
        {
            return Fail(
                GaussianBakeFailureCodes.UnsupportedOption,
                $"The color mode {BakeSplatPreviewOptions.OptimizedFitColorMode} is not supported by this engine version.");
        }

        // Load and check everything that can fail before the engine starts. A failure then exits with its
        // failure code and never races engine shutdown.
        CookedBakeInput input;
        AABB bounds;
        SceneNode stage;
        try
        {
            input = CookedBakeInputLoader.Load(options.InputPak, options.AssetPath, options.Kind);
            if (!BakeWorldFactory.TryComputeContentBounds(input, out bounds))
            {
                throw new GaussianBakeException(
                    GaussianBakeFailureCodes.ContentEmpty,
                    $"The asset '{options.AssetPath}' has no renderable mesh with valid bounds.");
            }

            stage = ResolveStage(options);
        }
        catch (GaussianBakeException ex)
        {
            return Fail(ex.FailureCode, ex.Message);
        }

        Console.WriteLine($"Gaussian preview bake: content bounds min={bounds.Min}, max={bounds.Max}.");
        try
        {
            return BakeRenderHost.Run(
                options,
                stage,
                (services, cancellationToken) => DumpViewsAsync(options, input, bounds, services, cancellationToken));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Gaussian preview bake failed with an unexpected engine error: {ex}");
            return UnexpectedExitCode;
        }
    }

    /// <summary>
    /// Returns the cooked stage from <c>--stage</c>, or the default stage when the option is not given.
    /// </summary>
    private static SceneNode ResolveStage(BakeSplatPreviewOptions options)
    {
        if (options.StagePak is null || options.StageAsset is null)
            return GaussianPreviewStage.CreateDefault();

        CookedBakeInput stageInput = CookedBakeInputLoader.Load(options.StagePak, options.StageAsset, GaussianBakeKind.Item);
        return stageInput.PrefabRoot
            ?? throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The stage asset '{options.StageAsset}' has no prefab root node.");
    }

    /// <summary>
    /// Renders <see cref="ViewCount"/> orbit views around the content bounds of the loaded input, and writes them
    /// as <c>debug/views/view_NN.png</c> in the output folder.
    /// </summary>
    private static async Task<int> DumpViewsAsync(
        BakeSplatPreviewOptions options,
        CookedBakeInput input,
        AABB bounds,
        IBakeRenderServices services,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BakeCameraPose> poses = BakeViewPlanner.PlanOrbitViews(bounds, ViewCount);
        float[][] views = await services.CaptureViewsAsync(input, poses, ViewWidth, ViewHeight, cancellationToken).ConfigureAwait(false);

        string directory = Path.Combine(Path.GetFullPath(options.OutputDirectory), "debug", "views");
        try
        {
            WriteViews(views, directory, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.EncodeFailed, $"The capture views cannot be written to '{directory}': {ex.Message}");
        }

        Console.WriteLine($"Wrote {views.Length} capture views to {directory}");
        Console.WriteLine($"Peak linear color channel in the captures (before the tonemap): {PeakColorChannel(views):F4}");
        return 0;
    }

    /// <summary>Returns the largest red, green, or blue value in the linear captures.</summary>
    private static float PeakColorChannel(float[][] views)
    {
        float peak = 0.0f;
        foreach (float[] view in views)
        {
            for (int i = 0; i < view.Length; i += 4)
            {
                float channel = MathF.Max(view[i], MathF.Max(view[i + 1], view[i + 2]));
                if (channel > peak)
                    peak = channel;
            }
        }

        return peak;
    }

    private static void WriteViews(float[][] views, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        IRuntimeImageCodec codec = RuntimeImageCodecs.Require();
        byte[] rgba8 = new byte[ViewWidth * ViewHeight * 4];
        for (int i = 0; i < views.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BakeTonemapper.ToSrgb8(views[i], rgba8);
            using RuntimeImage image = new(ViewWidth, ViewHeight, RuntimePixelFormat.Rgba8, rgba8);
            File.WriteAllBytes(Path.Combine(directory, $"view_{i:00}.png"), codec.EncodePng(image));
        }
    }

    private static int Fail(string failureCode, string message)
    {
        Console.Error.WriteLine($"Gaussian preview bake failed ({failureCode}): {message}");
        return GaussianBakeFailureCodes.ExitCodeFor(failureCode);
    }
}
