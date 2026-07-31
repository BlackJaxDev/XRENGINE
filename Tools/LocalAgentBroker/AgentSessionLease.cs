namespace XREngine.LocalAgentBroker;

/// <summary>
/// Releases a per-session read or mutation lease exactly once.
/// </summary>
internal sealed class AgentSessionLease(Func<ValueTask> release) : IAsyncDisposable
{
    private Func<ValueTask>? _release = release;

    public ValueTask DisposeAsync()
    {
        Func<ValueTask>? callback = Interlocked.Exchange(ref _release, null);
        return callback is null ? ValueTask.CompletedTask : callback();
    }
}
