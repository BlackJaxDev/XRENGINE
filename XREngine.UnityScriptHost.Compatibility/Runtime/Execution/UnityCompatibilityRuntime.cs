namespace XREngine.UnityScriptHost.Execution;

/// <summary>Connects shared API failures to the active authoring operation.</summary>
public static class UnityCompatibilityRuntime
{
    private static UnityCompatibilitySession? _activeSession;

    /// <summary>Associates one failure state with this worker process. Concurrent sessions are rejected.</summary>
    public static IDisposable BeginSession(UnityBuildExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var session = new UnityCompatibilitySession(state);
        if (Interlocked.CompareExchange(ref _activeSession, session, null) is not null)
            throw new InvalidOperationException("A compatibility session is already active in this worker process.");
        return session;
    }

    /// <summary>Records an unsupported call before returning the exception that its caller must throw.</summary>
    public static NotSupportedException Unsupported(string memberIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberIdentifier);
        Volatile.Read(ref _activeSession)?.State.FailUnsupportedOperation(memberIdentifier);
        return new NotSupportedException($"Unity member has no executable implementation: {memberIdentifier}.");
    }

    internal static void EndSession(UnityCompatibilitySession session)
    {
        UnityCompatibilitySession? previous = Interlocked.CompareExchange(ref _activeSession, null, session);
        if (previous is not null && !ReferenceEquals(previous, session))
            throw new InvalidOperationException("A different compatibility session owns this worker process.");
    }
}
