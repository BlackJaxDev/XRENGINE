namespace XREngine.Rendering.VideoStreaming;

/// <summary>
/// Writes streaming video diagnostics to the UI log and to the auxiliary log
/// <see cref="AuxiliaryLogName"/>.
/// </summary>
/// <remarks>
/// Release builds compile out the UI log, but they write auxiliary logs. Use this
/// type for cold-path messages that must stay visible in every build, such as the
/// selected frame uploader and rate-limited upload failures.
/// </remarks>
internal static class VideoStreamingDiagnostics
{
    /// <summary>The auxiliary log file in the session log folder.</summary>
    public const string AuxiliaryLogName = "video-streaming.log";

    /// <summary>Writes an informational message.</summary>
    public static void Info(string message)
    {
        Debug.UI(message);
        Debug.WriteAuxiliaryLog(AuxiliaryLogName, $"{DateTimeOffset.Now:O} {message}");
    }

    /// <summary>Writes a warning message.</summary>
    public static void Warning(string message)
    {
        Debug.UIWarning(message);
        Debug.WriteAuxiliaryLog(AuxiliaryLogName, $"{DateTimeOffset.Now:O} [WARN] {message}");
    }
}
