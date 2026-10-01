namespace XREngine.RenderBench;

/// <summary>Measured native recording and reuse decisions, separated from diagnostic observer policy.</summary>
public sealed record RenderBenchCommandBufferActivity(
    long PrimaryRecords,
    long PrimaryReuses,
    long SecondaryRecords,
    long SecondaryReuses,
    bool DiagnosticRecording);
