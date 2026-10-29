using System.Globalization;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;

namespace XREngine.Rendering.Models.Gaussian.Baking.Cli;

/// <summary>
/// Holds the command line options of the <c>--bake-splat-preview</c> editor command.
/// <see cref="TryParse"/> checks every value before the bake reads a file.
/// </summary>
public sealed class BakeSplatPreviewOptions
{
    /// <summary>The argument that selects the bake command.</summary>
    public const string CommandFlag = "--bake-splat-preview";

    /// <summary>The color mode that projects engine renders onto the splats.</summary>
    public const string LitProjectionColorMode = "lit-projection";

    /// <summary>The color mode that fits splat colors to the renders. This engine version does not support it.</summary>
    public const string OptimizedFitColorMode = "optimized-fit";

    /// <summary>The H.264 encoder that uses OpenH264 through ffmpeg.</summary>
    public const string OpenH264Encoder = "openh264";

    /// <summary>The H.264 encoder that uses NVIDIA NVENC through ffmpeg.</summary>
    public const string NvencEncoder = "nvenc";

    /// <summary>The ffmpeg command that runs when no path is given.</summary>
    public const string DefaultFfmpegCommand = "ffmpeg";

    /// <summary>The separator between the archive path and the asset path in <c>--stage</c>.</summary>
    public const string StageSeparator = "::";

    private const string InputFlag = "--input";
    private const string AssetFlag = "--asset";
    private const string KindFlag = "--kind";
    private const string OutputFlag = "--output";
    private const string ClipFlag = "--clip";
    private const string ColorModeFlag = "--color-mode";
    private const string StageFlag = "--stage";
    private const string FfmpegFlag = "--ffmpeg";
    private const string H264EncoderFlag = "--h264-encoder";
    private const string SplatsFlag = "--splats";
    private const string DumpViewsOnlyFlag = "--dump-views-only";

    private static readonly string[] ValueFlags =
    [
        InputFlag,
        AssetFlag,
        KindFlag,
        OutputFlag,
        ClipFlag,
        ColorModeFlag,
        StageFlag,
        FfmpegFlag,
        H264EncoderFlag,
        SplatsFlag,
    ];

    private BakeSplatPreviewOptions()
    {
    }

    /// <summary>Gets the path of the cooked <c>.pak</c> archive to bake.</summary>
    public string InputPak { get; private init; } = string.Empty;

    /// <summary>Gets the path of the prefab or world asset in the archive.</summary>
    public string AssetPath { get; private init; } = string.Empty;

    /// <summary>Gets the type of content to bake.</summary>
    public GaussianBakeKind Kind { get; private init; }

    /// <summary>Gets the folder that receives the bake outputs.</summary>
    public string OutputDirectory { get; private init; } = string.Empty;

    /// <summary>Gets the name of the animation clip to bake, or null to select a clip automatically.</summary>
    public string? ClipName { get; private init; }

    /// <summary>Gets the splat color mode. The default is <see cref="LitProjectionColorMode"/>.</summary>
    public string ColorMode { get; private init; } = LitProjectionColorMode;

    /// <summary>Gets the path of the cooked archive that holds the preview stage, or null for the default stage.</summary>
    public string? StagePak { get; private init; }

    /// <summary>Gets the path of the stage prefab in <see cref="StagePak"/>, or null for the default stage.</summary>
    public string? StageAsset { get; private init; }

    /// <summary>
    /// Gets the ffmpeg executable. It comes from <c>--ffmpeg</c>, then from the <c>XRE_FFMPEG_PATH</c>
    /// environment variable, then it is <see cref="DefaultFfmpegCommand"/>.
    /// </summary>
    public string FfmpegPath { get; private init; } = DefaultFfmpegCommand;

    /// <summary>Gets the H.264 encoder. The default is <see cref="OpenH264Encoder"/>.</summary>
    public string H264Encoder { get; private init; } = OpenH264Encoder;

    /// <summary>Gets the number of splats at full detail, or null to use the splat budget of the kind.</summary>
    public int? SplatCount { get; private init; }

    /// <summary>Gets a value that tells the bake to write the capture views as PNG files and stop.</summary>
    public bool DumpViewsOnly { get; private init; }

    /// <summary>
    /// Reads the bake options from command line arguments. The <see cref="CommandFlag"/> argument is accepted and ignored.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <param name="options">The options when the arguments are valid; otherwise null.</param>
    /// <param name="error">A message that names the incorrect option when the arguments are not valid; otherwise null.</param>
    /// <returns>True when the arguments are valid.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out BakeSplatPreviewOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool dumpViewsOnly = false;
        bool commandFlagSeen = false;
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i] ?? string.Empty;
            if (string.Equals(argument, CommandFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (commandFlagSeen)
                    return Fail($"The {CommandFlag} option is given more than one time.", out error);
                commandFlagSeen = true;
                continue;
            }

            if (string.Equals(argument, DumpViewsOnlyFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (dumpViewsOnly)
                    return Fail($"The {DumpViewsOnlyFlag} option is given more than one time.", out error);
                dumpViewsOnly = true;
                continue;
            }

            string? flag = FindValueFlag(argument);
            if (flag is null)
                return Fail($"The argument '{argument}' is not a known option.", out error);
            if (values.ContainsKey(flag))
                return Fail($"The {flag} option is given more than one time.", out error);
            if (i + 1 >= args.Count || IsFlag(args[i + 1]))
                return Fail($"The {flag} option needs a value.", out error);

            values[flag] = args[++i] ?? string.Empty;
        }

        if (!TryGetRequired(values, InputFlag, out string inputPak, out error)
            || !TryGetRequired(values, AssetFlag, out string assetPath, out error)
            || !TryGetRequired(values, KindFlag, out string kindName, out error)
            || !TryGetRequired(values, OutputFlag, out string outputDirectory, out error))
        {
            return false;
        }

        if (!TryParseKind(kindName, out GaussianBakeKind kind))
            return Fail($"The {KindFlag} option must be avatar, item, or world, not '{kindName}'.", out error);

        if (!TryGetOptional(values, ClipFlag, out string? clipName, out error)
            || !TryGetOptional(values, ColorModeFlag, out string? colorMode, out error)
            || !TryGetOptional(values, StageFlag, out string? stage, out error)
            || !TryGetOptional(values, FfmpegFlag, out string? ffmpegPath, out error)
            || !TryGetOptional(values, H264EncoderFlag, out string? h264Encoder, out error)
            || !TryGetOptional(values, SplatsFlag, out string? splats, out error))
        {
            return false;
        }

        colorMode ??= LitProjectionColorMode;
        if (!IsOneOf(colorMode, LitProjectionColorMode, OptimizedFitColorMode))
            return Fail($"The {ColorModeFlag} option must be {LitProjectionColorMode} or {OptimizedFitColorMode}, not '{colorMode}'.", out error);

        h264Encoder ??= OpenH264Encoder;
        if (!IsOneOf(h264Encoder, OpenH264Encoder, NvencEncoder))
            return Fail($"The {H264EncoderFlag} option must be {OpenH264Encoder} or {NvencEncoder}, not '{h264Encoder}'.", out error);

        string? stagePak = null;
        string? stageAsset = null;
        if (stage is not null && !TrySplitStage(stage, out stagePak, out stageAsset))
            return Fail($"The {StageFlag} option must have the form <pak>{StageSeparator}<asset>, not '{stage}'.", out error);

        int? splatCount = null;
        if (splats is not null)
        {
            int maxSplats = GaussianBakeBudget.For(kind).MaxSplats;
            if (!int.TryParse(splats, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > maxSplats)
            {
                return Fail(
                    $"The {SplatsFlag} option must be a whole number from 1 to " +
                    $"{maxSplats.ToString(CultureInfo.InvariantCulture)} for kind {kindName.ToLowerInvariant()}, not '{splats}'.",
                    out error);
            }

            splatCount = count;
        }

        options = new BakeSplatPreviewOptions
        {
            InputPak = inputPak,
            AssetPath = assetPath,
            Kind = kind,
            OutputDirectory = outputDirectory,
            ClipName = clipName,
            ColorMode = colorMode.ToLowerInvariant(),
            StagePak = stagePak,
            StageAsset = stageAsset,
            FfmpegPath = ffmpegPath ?? ResolveDefaultFfmpegPath(),
            H264Encoder = h264Encoder.ToLowerInvariant(),
            SplatCount = splatCount,
            DumpViewsOnly = dumpViewsOnly,
        };
        error = null;
        return true;
    }

    private static string ResolveDefaultFfmpegPath()
    {
        string? environmentPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.FfmpegPath);
        return string.IsNullOrWhiteSpace(environmentPath) ? DefaultFfmpegCommand : environmentPath;
    }

    private static string? FindValueFlag(string argument)
    {
        foreach (string flag in ValueFlags)
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
                return flag;
        return null;
    }

    private static bool IsFlag(string? argument)
        => argument is not null && argument.StartsWith("--", StringComparison.Ordinal);

    private static bool TryGetRequired(Dictionary<string, string> values, string flag, out string value, out string? error)
    {
        if (!values.TryGetValue(flag, out string? raw))
        {
            value = string.Empty;
            return Fail($"The {flag} option is necessary.", out error);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            value = string.Empty;
            return Fail($"The {flag} option needs a value.", out error);
        }

        value = raw;
        error = null;
        return true;
    }

    private static bool TryGetOptional(Dictionary<string, string> values, string flag, out string? value, out string? error)
    {
        if (!values.TryGetValue(flag, out value))
        {
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = null;
            return Fail($"The {flag} option needs a value.", out error);
        }

        error = null;
        return true;
    }

    private static bool TryParseKind(string name, out GaussianBakeKind kind)
    {
        if (string.Equals(name, "avatar", StringComparison.OrdinalIgnoreCase))
            kind = GaussianBakeKind.Avatar;
        else if (string.Equals(name, "item", StringComparison.OrdinalIgnoreCase))
            kind = GaussianBakeKind.Item;
        else if (string.Equals(name, "world", StringComparison.OrdinalIgnoreCase))
            kind = GaussianBakeKind.World;
        else
        {
            kind = default;
            return false;
        }

        return true;
    }

    private static bool TrySplitStage(string stage, out string? stagePak, out string? stageAsset)
    {
        stagePak = null;
        stageAsset = null;
        int separator = stage.IndexOf(StageSeparator, StringComparison.Ordinal);
        if (separator < 0)
            return false;

        string pak = stage[..separator];
        string asset = stage[(separator + StageSeparator.Length)..];
        if (string.IsNullOrWhiteSpace(pak) || string.IsNullOrWhiteSpace(asset))
            return false;

        stagePak = pak;
        stageAsset = asset;
        return true;
    }

    private static bool IsOneOf(string value, string first, string second)
        => string.Equals(value, first, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, second, StringComparison.OrdinalIgnoreCase);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
