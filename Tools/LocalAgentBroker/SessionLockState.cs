namespace XREngine.LocalAgentBroker;

/// <summary>
/// Fair asynchronous reader/writer state for one editor session.
/// </summary>
internal sealed class SessionLockState
{
    public SemaphoreSlim Turnstile { get; } = new(1, 1);

    public SemaphoreSlim RoomEmpty { get; } = new(1, 1);

    public SemaphoreSlim ReaderMutex { get; } = new(1, 1);

    public int ReaderCount { get; set; }
}
