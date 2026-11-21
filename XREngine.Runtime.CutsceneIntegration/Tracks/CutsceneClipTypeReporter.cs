namespace XREngine.Cutscenes;

/// <summary>
/// Reports a clip of the wrong type for its track. Each clip is reported once,
/// so a per-frame evaluation never adds the same diagnostic again.
/// </summary>
internal sealed class CutsceneClipTypeReporter
{
    private readonly List<CutsceneCompileDiagnostic> _diagnostics;
    private readonly HashSet<(int Track, int Clip)> _reported = [];

    public CutsceneClipTypeReporter(List<CutsceneCompileDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void Report(int trackIndex, int clipIndex, string trackName, string expectedType, CutsceneClip clip)
    {
        if (!_reported.Add((trackIndex, clipIndex)))
            return;

        string message = $"The clip {clipIndex} on track '{trackName}' is a {clip.GetType().Name}. The track needs a {expectedType}. The clip is skipped.";
        _diagnostics.Add(new CutsceneCompileDiagnostic(trackIndex, clipIndex, message));
        Debug.LogWarning(ELogCategory.Animation, "Cutscene evaluation: {0}", message);
    }
}
