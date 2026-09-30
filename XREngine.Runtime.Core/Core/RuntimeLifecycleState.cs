namespace XREngine;

/// <summary>
/// Canonical process-runtime lifecycle state. Application hosts own composition and use this
/// state to coordinate startup, shutdown requests, and deterministic teardown.
/// </summary>
public sealed class RuntimeLifecycleState
{
    private int _startingUp;
    private int _shuttingDown;
    private int _shutdownRequested;

    public static RuntimeLifecycleState Current { get; } = new();

    public bool StartingUp => Volatile.Read(ref _startingUp) != 0;
    public bool ShuttingDown => Volatile.Read(ref _shuttingDown) != 0;
    public bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public void BeginStartup()
    {
        Interlocked.Exchange(ref _shutdownRequested, 0);
        Interlocked.Exchange(ref _shuttingDown, 0);
        Interlocked.Exchange(ref _startingUp, 1);
    }

    public void CompleteStartup()
        => Interlocked.Exchange(ref _startingUp, 0);

    public void RequestShutdown()
        => Interlocked.Exchange(ref _shutdownRequested, 1);

    public bool TryBeginShutdown()
    {
        Interlocked.Exchange(ref _startingUp, 0);
        return Interlocked.CompareExchange(ref _shuttingDown, 1, 0) == 0;
    }
}
