using System.Globalization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Validated configuration for one viewport sequence capture session.
/// </summary>
internal sealed class ViewportSequenceCaptureOptions
{
    public const int MaximumFrames = 300;
    public const double MaximumDurationSeconds = 60.0;
    public const int MaximumFrameStride = 600;
    public const double MaximumCaptureFramesPerSecond = 240.0;
    public const int MaximumInFlightReadbacks = 8;
    public const long MaximumTotalOutputPixels = 250_000_000L;
    public const long MaximumInFlightReadbackBytes = 256L * 1024L * 1024L;
    public const int MaximumContactSheetPixels = 64 * 1024 * 1024;
    public const int MaximumRetainedSessions = 128;

    public int? FrameCount { get; private init; }
    public double? DurationSeconds { get; private init; }
    public int FrameStride { get; private init; }
    public double? CaptureFramesPerSecond { get; private init; }
    public int MaxFrames { get; private init; }
    public double OutputScale { get; private init; }
    public int MaxInFlightReadbacks { get; private init; }
    public ViewportSequenceCaptureOverflowPolicy OverflowPolicy { get; private init; }
    public bool PreserveAlpha { get; private init; }
    public bool CreateContactSheet { get; private init; }
    public int ContactSheetColumns { get; private init; }
    public int ContactSheetThumbnailWidth { get; private init; }
    public bool ComputeFrameDifferences { get; private init; }
    public string OutputRootDirectory { get; private init; } = string.Empty;

    public bool IsDurationBased => DurationSeconds.HasValue;
    public int CaptureLimit => FrameCount ?? MaxFrames;

    /// <summary>
    /// Validates untrusted MCP arguments and constructs immutable capture options.
    /// </summary>
    public static bool TryCreate(
        int? frameCount,
        double? durationSeconds,
        int frameStride,
        double? captureFramesPerSecond,
        int maxFrames,
        double outputScale,
        int maxInFlightReadbacks,
        string overflowPolicy,
        bool preserveAlpha,
        bool createContactSheet,
        int contactSheetColumns,
        int contactSheetThumbnailWidth,
        bool computeFrameDifferences,
        string? outputDirectory,
        out ViewportSequenceCaptureOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (frameCount.HasValue == durationSeconds.HasValue)
        {
            error = "Provide exactly one stop condition: frame_count or duration_seconds.";
            return false;
        }

        if (frameCount is < 1 or > MaximumFrames)
        {
            error = $"frame_count must be between 1 and {MaximumFrames}.";
            return false;
        }

        if (durationSeconds.HasValue &&
            (!double.IsFinite(durationSeconds.Value) || durationSeconds.Value <= 0.0 || durationSeconds.Value > MaximumDurationSeconds))
        {
            error = $"duration_seconds must be finite and greater than 0, up to {MaximumDurationSeconds.ToString(CultureInfo.InvariantCulture)} seconds.";
            return false;
        }

        if (frameStride is < 1 or > MaximumFrameStride)
        {
            error = $"frame_stride must be between 1 and {MaximumFrameStride}.";
            return false;
        }

        if (captureFramesPerSecond.HasValue &&
            (!double.IsFinite(captureFramesPerSecond.Value) || captureFramesPerSecond.Value <= 0.0 || captureFramesPerSecond.Value > MaximumCaptureFramesPerSecond))
        {
            error = $"capture_fps must be finite and greater than 0, up to {MaximumCaptureFramesPerSecond.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        if (maxFrames is < 1 or > MaximumFrames)
        {
            error = $"max_frames must be between 1 and {MaximumFrames}.";
            return false;
        }

        if (!double.IsFinite(outputScale) || outputScale < 0.1 || outputScale > 1.0)
        {
            error = "output_scale must be finite and between 0.1 and 1.0.";
            return false;
        }

        if (maxInFlightReadbacks is < 1 or > MaximumInFlightReadbacks)
        {
            error = $"max_in_flight_readbacks must be between 1 and {MaximumInFlightReadbacks}.";
            return false;
        }

        if (!Enum.TryParse(overflowPolicy, ignoreCase: true, out ViewportSequenceCaptureOverflowPolicy parsedOverflowPolicy))
        {
            error = "overflow_policy must be 'fail' or 'drop'.";
            return false;
        }

        if (contactSheetColumns is < 0 or > 32)
        {
            error = "contact_sheet_columns must be 0 (automatic) or between 1 and 32.";
            return false;
        }

        if (contactSheetThumbnailWidth is < 64 or > 1024)
        {
            error = "contact_sheet_thumbnail_width must be between 64 and 1024 pixels.";
            return false;
        }

        string rootDirectory;
        try
        {
            rootDirectory = Path.GetFullPath(outputDirectory ?? Path.Combine(
                Environment.CurrentDirectory,
                "Build",
                "_AgentValidation",
                "mcp-viewport-sequences"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"output_dir is invalid: {ex.Message}";
            return false;
        }

        options = new ViewportSequenceCaptureOptions
        {
            FrameCount = frameCount,
            DurationSeconds = durationSeconds,
            FrameStride = frameStride,
            CaptureFramesPerSecond = captureFramesPerSecond,
            MaxFrames = frameCount ?? maxFrames,
            OutputScale = outputScale,
            MaxInFlightReadbacks = maxInFlightReadbacks,
            OverflowPolicy = parsedOverflowPolicy,
            PreserveAlpha = preserveAlpha,
            CreateContactSheet = createContactSheet,
            ContactSheetColumns = contactSheetColumns,
            ContactSheetThumbnailWidth = contactSheetThumbnailWidth,
            ComputeFrameDifferences = computeFrameDifferences,
            OutputRootDirectory = rootDirectory,
        };
        return true;
    }
}
