namespace XREngine;

/// <summary>
/// Host-independent dispatch boundary for work that must run on the application thread.
/// </summary>
public interface IRuntimeThreadServices
{
    /// <summary>Attempts to enqueue work on the application thread.</summary>
    /// <returns>
    /// <see langword="true"/> only when the host accepted the action for queued execution.
    /// A <see langword="false"/> result lets fallback callers execute the action themselves when
    /// <paramref name="executeNowIfAlreadyAppThread"/> is false. With immediate execution enabled,
    /// a direct host may already have run the action inline and the caller must not apply a fallback.
    /// </returns>
    bool InvokeOnAppThread(Action action, string? reason = null, bool executeNowIfAlreadyAppThread = false);

    /// <summary>Enqueues work that must run on the host update thread.</summary>
    void EnqueueUpdateThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    /// <summary>Enqueues work that must run on the host physics thread.</summary>
    void EnqueuePhysicsThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

public static class RuntimeThreadServices
{
    private static IRuntimeThreadServices _current = new DirectRuntimeThreadServices();

    public static IRuntimeThreadServices Current
    {
        get => _current;
        set => _current = value ?? new DirectRuntimeThreadServices();
    }

    private sealed class DirectRuntimeThreadServices : IRuntimeThreadServices
    {
        public bool InvokeOnAppThread(Action action, string? reason = null, bool executeNowIfAlreadyAppThread = false)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (executeNowIfAlreadyAppThread)
                action();
            return false;
        }
    }
}