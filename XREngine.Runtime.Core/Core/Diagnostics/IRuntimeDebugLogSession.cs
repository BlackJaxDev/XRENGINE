namespace XREngine;

/// <summary>Owns a native log run and opens its text files.</summary>
/// <remarks>
/// Debug calls session and text-log methods while it holds its writer lock.
/// Implementations must not call Debug or wait for a thread that needs that lock.
/// </remarks>
public interface IRuntimeDebugLogSession : IDisposable
{
    bool HasSessionId { get; }

    void SetSessionIdIfAbsent(string sessionId);

    string EnsureRunDirectory();

    IRuntimeDebugTextLog OpenTextLog(string fileName);
}
