using XREngine.Data.Geometry;
using XREngine.Imaging;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Cli;
using XREngine.Rendering.Models.Gaussian.Baking.Imaging;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Rendering;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Runs the <c>--bake-splat-preview</c> editor command. The command reads a cooked prefab or world from a
/// <c>.pak</c> archive and renders it in a hidden engine window.
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
        if (!BakeSplatPreviewOptions.TryParse(args, out BakeSplatPreviewOptions? options, out string? error) || options is null)
            return Fail(GaussianBakeFailureCodes.InvalidArguments, error ?? "The arguments are not valid.");

        if (string.Equals(options.ColorMode, BakeSplatPreviewOptions.OptimizedFitColorMode, StringComparison.Ordinal))
        {
            return Fail(
                GaussianBakeFailureCodes.UnsupportedOption,
                $"The color mode {BakeSplatPreviewOptions.OptimizedFitColorMode} is not supported by this engine version.");
        }

        if (!options.DumpViewsOnly)
            return Fail(GaussianBakeFailureCodes.UnsupportedOption, "This engine version supports only --dump-views-only.");

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
