namespace XREngine.Rendering.Commands;

/// <summary>One paged read of the fixed S13a event ring.</summary>
public readonly record struct S13aPublicationTraceSnapshot(
    bool Enabled, uint TracedCommandId, long StopwatchFrequency, int Capacity,
    long LatestSequence, long EarliestAvailableSequence, long NextSequence,
    long OverwrittenEventCount, long BusyDropCount, long DroppedInPage,
    S13aPublicationTraceEvent[] Events);
