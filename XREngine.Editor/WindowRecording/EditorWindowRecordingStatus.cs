namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Snapshot of an active or recently finished editor window video recording session.
/// </summary>
public sealed record EditorWindowRecordingStatus(
    bool IsRecording,
    string? OutputPath,
    string Backend,
    double ElapsedSeconds,
    long CapturedFrameCount,
    long WrittenVideoFrameCount,
    long DroppedReadbackCount,
    long FileSizeBytes,
    int FramesPerSecond,
    int MaxWidth,
    int MaxHeight,
    string? LastError);
