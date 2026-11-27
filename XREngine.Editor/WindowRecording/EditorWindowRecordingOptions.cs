using System.Globalization;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Configuration for one editor window MP4 recording session.
/// </summary>
public sealed record EditorWindowRecordingOptions
{
    /// <summary>Default output frame rate, in frames per second.</summary>
    public const int DefaultFramesPerSecond = 60;

    /// <summary>Default maximum output width, in pixels.</summary>
    public const int DefaultMaxWidth = 1920;

    /// <summary>Default maximum output height, in pixels.</summary>
    public const int DefaultMaxHeight = 1080;

    /// <summary>Default H.264 constant rate factor.</summary>
    public const int DefaultCrf = 26;

    /// <summary>Default H.264 peak bitrate cap.</summary>
    public const string DefaultMaxBitrate = "8M";

    /// <summary>Default libx264 encoder speed preset.</summary>
    public const string DefaultPreset = "faster";

    /// <summary>Gets the target output frame rate, in frames per second.</summary>
    public int FramesPerSecond { get; init; } = DefaultFramesPerSecond;

    /// <summary>Gets the maximum output width, in pixels. Larger frames scale down proportionally.</summary>
    public int MaxWidth { get; init; } = DefaultMaxWidth;

    /// <summary>Gets the maximum output height, in pixels. Larger frames scale down proportionally.</summary>
    public int MaxHeight { get; init; } = DefaultMaxHeight;

    /// <summary>Gets the libx264 CRF quality target (lower is higher quality and larger file size).</summary>
    public int Crf { get; init; } = DefaultCrf;

    /// <summary>Gets the peak bitrate limit passed to ffmpeg <c>-maxrate</c> (for example <c>3M</c> or <c>8M</c>).</summary>
    public string MaxBitrate { get; init; } = DefaultMaxBitrate;

    /// <summary>Gets the libx264 preset (for example <c>veryfast</c> or <c>faster</c>).</summary>
    public string Preset { get; init; } = DefaultPreset;

    /// <summary>Gets the explicit MP4 destination path, or null to use a time-stamped file in <c>Videos/XRENGINE</c>.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Gets the capture backend selection.</summary>
    public EEditorWindowRecordingBackend Backend { get; init; } = EEditorWindowRecordingBackend.Auto;

    /// <summary>Gets the H.264 video encoder selection (<see cref="EEditorWindowVideoEncoder.Auto"/> prefers NVENC when available).</summary>
    public EEditorWindowVideoEncoder Encoder { get; init; } = EEditorWindowVideoEncoder.Auto;

    /// <summary>Gets an optional automatic stop duration. Null records until stopped manually or on shutdown.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Gets whether Windows Graphics Capture includes the OS mouse cursor.</summary>
    public bool IncludeCursor { get; init; } = true;

    /// <summary>
    /// Returns a validated copy with clamped numeric bounds and non-empty encoder parameters.
    /// </summary>
    public EditorWindowRecordingOptions Normalize()
    {
        int fps = Math.Clamp(FramesPerSecond, 1, 120);
        int maxWidth = Math.Clamp(MaxWidth, 160, 7680);
        int maxHeight = Math.Clamp(MaxHeight, 120, 4320);
        int crf = Math.Clamp(Crf, 10, 40);
        string maxBitrate = string.IsNullOrWhiteSpace(MaxBitrate) ? DefaultMaxBitrate : MaxBitrate.Trim();
        string preset = string.IsNullOrWhiteSpace(Preset) ? DefaultPreset : Preset.Trim();
        TimeSpan? maxDuration = MaxDuration is { } duration && duration > TimeSpan.Zero ? duration : null;
        string? outputPath = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFullPath(OutputPath.Trim());

        return this with
        {
            FramesPerSecond = fps,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Crf = crf,
            MaxBitrate = maxBitrate,
            Preset = preset,
            MaxDuration = maxDuration,
            OutputPath = outputPath,
        };
    }

    /// <summary>
    /// Parses a user or environment backend string (<c>auto</c>, <c>wgc</c>, <c>gfxcapture</c>,
    /// <c>readback</c>, or enum member names).
    /// </summary>
    public static bool TryParseBackend(string? value, out EEditorWindowRecordingBackend backend)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            backend = EEditorWindowRecordingBackend.Auto;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                backend = EEditorWindowRecordingBackend.Auto;
                return true;
            case "wgc":
            case "gfxcapture":
            case "windowsgraphicscapture":
                backend = EEditorWindowRecordingBackend.WindowsGraphicsCapture;
                return true;
            case "readback":
            case "rendererreadback":
            case "gpu":
            case "gpureadback":
                backend = EEditorWindowRecordingBackend.RendererReadback;
                return true;
            default:
                backend = EEditorWindowRecordingBackend.Auto;
                return false;
        }
    }

    /// <summary>
    /// Parses a user or environment video encoder string (<c>auto</c>, <c>nvenc</c>, <c>h264_nvenc</c>,
    /// <c>x264</c>, or <c>libx264</c>).
    /// </summary>
    public static bool TryParseEncoder(string? value, out EEditorWindowVideoEncoder encoder)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            encoder = EEditorWindowVideoEncoder.Auto;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                encoder = EEditorWindowVideoEncoder.Auto;
                return true;
            case "nvenc":
            case "h264_nvenc":
                encoder = EEditorWindowVideoEncoder.Nvenc;
                return true;
            case "x264":
            case "libx264":
            case "cpu":
                encoder = EEditorWindowVideoEncoder.X264;
                return true;
            default:
                encoder = EEditorWindowVideoEncoder.Auto;
                return false;
        }
    }

    /// <summary>
    /// Reads startup auto-recording options from environment variables when
    /// <see cref="XREngineEnvironmentVariables.RecordVideoOnStartup"/> is set.
    /// Returns null when startup recording is not requested.
    /// </summary>
    public static EditorWindowRecordingOptions? FromStartupEnvironment()
    {
        string? outputPath = XREnvironment.GetValue(XREngineEnvironmentVariables.RecordVideoOnStartup);
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;

        string? backendText = XREnvironment.GetValue(XREngineEnvironmentVariables.RecordVideoBackend);
        if (!TryParseBackend(backendText, out EEditorWindowRecordingBackend backend))
            backend = EEditorWindowRecordingBackend.Auto;

        string? encoderText = XREnvironment.GetValue(XREngineEnvironmentVariables.RecordVideoEncoder);
        if (!TryParseEncoder(encoderText, out EEditorWindowVideoEncoder encoder))
            encoder = EEditorWindowVideoEncoder.Auto;

        int fps = ReadIntEnv(XREngineEnvironmentVariables.RecordVideoFps, DefaultFramesPerSecond);
        int maxWidth = ReadIntEnv(XREngineEnvironmentVariables.RecordVideoMaxWidth, DefaultMaxWidth);
        int maxHeight = ReadIntEnv(XREngineEnvironmentVariables.RecordVideoMaxHeight, DefaultMaxHeight);
        int crf = ReadIntEnv(XREngineEnvironmentVariables.RecordVideoCrf, DefaultCrf);
        string maxBitrate = XREnvironment.GetValue(XREngineEnvironmentVariables.RecordVideoMaxBitrate) ?? DefaultMaxBitrate;

        TimeSpan? maxDuration = null;
        string? secondsText = XREnvironment.GetValue(XREngineEnvironmentVariables.RecordVideoSeconds);
        if (!string.IsNullOrWhiteSpace(secondsText) &&
            double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            seconds > 0.0)
        {
            maxDuration = TimeSpan.FromSeconds(seconds);
        }

        return new EditorWindowRecordingOptions
        {
            OutputPath = outputPath,
            Backend = backend,
            Encoder = encoder,
            FramesPerSecond = fps,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Crf = crf,
            MaxBitrate = maxBitrate,
            MaxDuration = maxDuration,
        }.Normalize();
    }

    private static int ReadIntEnv(string variableName, int defaultValue)
    {
        string? raw = XREnvironment.GetValue(variableName);
        return !string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }
}
