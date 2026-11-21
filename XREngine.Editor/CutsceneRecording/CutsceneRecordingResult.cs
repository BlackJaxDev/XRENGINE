namespace XREngine.Editor.CutsceneRecording;

/// <summary>The files and the phase timings of one finished cutscene recording.</summary>
/// <param name="MasterVideoPath">The master MP4 video.</param>
/// <param name="PosterPath">The poster PNG.</param>
/// <param name="OptimizedVideoPath">The optimized file size copy, or null when the settings turn it off.</param>
/// <param name="ContactSheetPath">The contact sheet PNG, or null when the settings turn it off.</param>
/// <param name="FrameCount">The number of recorded frames.</param>
/// <param name="WarmupSeconds">The wall time of the shot warm-up, in seconds.</param>
/// <param name="CaptureSeconds">The wall time of the capture and the master encode, in seconds.</param>
/// <param name="OptimizedEncodeSeconds">The wall time of the optimized file size encode, in seconds.</param>
/// <param name="ContactSheetSeconds">The wall time of the contact sheet, in seconds.</param>
internal sealed record CutsceneRecordingResult(
    string MasterVideoPath,
    string PosterPath,
    string? OptimizedVideoPath,
    string? ContactSheetPath,
    int FrameCount,
    double WarmupSeconds,
    double CaptureSeconds,
    double OptimizedEncodeSeconds,
    double ContactSheetSeconds);
