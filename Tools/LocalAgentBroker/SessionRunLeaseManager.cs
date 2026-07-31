using System.Collections.Concurrent;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Allows overlapping reads while serializing mutations against all work on a session.
/// </summary>
internal sealed class SessionRunLeaseManager
{
    private readonly ConcurrentDictionary<string, SessionLockState> _states =
        new(StringComparer.Ordinal);

    public Task<AgentSessionLease> AcquireAsync(
        string sessionName,
        bool mutation,
        CancellationToken cancellationToken)
    {
        SessionLockState state = _states.GetOrAdd(sessionName, static _ => new SessionLockState());
        return mutation
            ? AcquireWriteAsync(state, cancellationToken)
            : AcquireReadAsync(state, cancellationToken);
    }

    private static async Task<AgentSessionLease> AcquireReadAsync(
        SessionLockState state,
        CancellationToken cancellationToken)
    {
        await state.Turnstile.WaitAsync(cancellationToken);
        state.Turnstile.Release();

        await state.ReaderMutex.WaitAsync(cancellationToken);
        try
        {
            state.ReaderCount++;
            if (state.ReaderCount == 1)
            {
                try
                {
                    await state.RoomEmpty.WaitAsync(cancellationToken);
                }
                catch
                {
                    state.ReaderCount--;
                    throw;
                }
            }
        }
        finally
        {
            state.ReaderMutex.Release();
        }

        return new AgentSessionLease(async () =>
        {
            await state.ReaderMutex.WaitAsync();
            try
            {
                state.ReaderCount--;
                if (state.ReaderCount == 0)
                    state.RoomEmpty.Release();
            }
            finally
            {
                state.ReaderMutex.Release();
            }
        });
    }

    private static async Task<AgentSessionLease> AcquireWriteAsync(
        SessionLockState state,
        CancellationToken cancellationToken)
    {
        await state.Turnstile.WaitAsync(cancellationToken);
        try
        {
            await state.RoomEmpty.WaitAsync(cancellationToken);
        }
        catch
        {
            state.Turnstile.Release();
            throw;
        }

        return new AgentSessionLease(() =>
        {
            state.RoomEmpty.Release();
            state.Turnstile.Release();
            return ValueTask.CompletedTask;
        });
    }
}
