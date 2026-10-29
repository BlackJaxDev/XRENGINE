namespace XREngine.Execution;

/// <summary>Owns the lifetime of the three dedicated engine loop workers.</summary>
public interface IEngineLoopDomain
{
    bool HasLiveThreads { get; }

    /// <summary>Starts update, visibility, then fixed update. A failed start retains all started workers.</summary>
    void Start();

    /// <summary>
    /// Waits within one shared non-negative timeout after the caller signals stop.
    /// A current worker cannot join itself. Incomplete workers remain owned for retry.
    /// </summary>
    EngineLoopExitStatus WaitForExit(TimeSpan timeout);
}
