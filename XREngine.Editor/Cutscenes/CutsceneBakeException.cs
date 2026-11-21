namespace XREngine.Editor.Cutscenes;

/// <summary>A residency manifest bake that failed its T0 budget. The message lists the largest assets.</summary>
public sealed class CutsceneBakeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="report">The T0 report of the failed bake.</param>
    public CutsceneBakeException(CutsceneT0Report report)
        : base($"The cutscene is above its T0 budget, so the bake failed. {report?.Describe()}")
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }

    /// <summary>The T0 report of the failed bake.</summary>
    public CutsceneT0Report Report { get; }
}
