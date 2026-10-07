namespace XREngine;

/// <summary>Supplies native log sessions and render-thread dispatch.</summary>
/// <remarks>
/// Debug can create a session while it holds its writer lock. A backend must
/// not call Debug or wait for a thread that needs that lock from CreateSession.
/// Queue methods must keep normal ThreadPool execution-context flow and must
/// not run callbacks inline. Each queued callback uses the backend captured
/// when the log operation entered Debug. A later provider change can affect
/// dispatch while open files remain with their captured session provider.
/// </remarks>
public interface IRuntimeDebugLogBackend
{
    IRuntimeDebugLogSession CreateSession(RuntimeDebugLogSessionOptions options);

    void QueueOut(WaitCallback callback, object state);

    void QueueCategory(WaitCallback callback, object state);

    void QueueAuxiliary(WaitCallback callback, object state);
}
