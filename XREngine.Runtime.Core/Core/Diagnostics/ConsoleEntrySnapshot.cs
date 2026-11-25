namespace XREngine;

/// <summary>Retains one log entry and its repeat count from the locked console snapshot.</summary>
public readonly record struct ConsoleEntrySnapshot(LogEntry Entry, int RepeatCount);
