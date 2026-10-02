namespace XREngine.Data.Runtime.AotParity;

/// <summary>
/// Ambient scope that marks the current logical call chain as player-path work. Dispose exits the
/// scope. The scope follows asynchronous continuations so a published load that awaits still
/// reports fallbacks on its continuation.
/// </summary>
public readonly struct AotParityPlayerPathScope : IDisposable
{
    private readonly bool _entered;
    private readonly bool _synchronous;

    internal AotParityPlayerPathScope(bool entered, bool synchronous = false)
    {
        _entered = entered;
        _synchronous = synchronous;
    }

    public void Dispose()
    {
        if (_entered)
            AotParityDiagnostics.ExitPlayerPath(_synchronous);
    }
}
