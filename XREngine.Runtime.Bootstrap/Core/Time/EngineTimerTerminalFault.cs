using System;

namespace XREngine.Timers;

/// <summary>
/// Immutable evidence retained for the first fault that terminates an engine timer run.
/// </summary>
public sealed record EngineTimerTerminalFault(
    string Loop,
    string Phase,
    DateTime TimestampUtc,
    int ManagedThreadId,
    string ExceptionType,
    string ExceptionMessage,
    string ExceptionDetail,
    long RequestedCollectGeneration,
    long CompletedCollectGeneration,
    long PublishedCollectGeneration,
    long ConsumedCollectGeneration);
