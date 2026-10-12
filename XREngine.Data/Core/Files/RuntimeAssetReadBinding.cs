namespace XREngine.Data;

/// <summary>Identity and cancellation lifetime shared by reads from one immutable installation.</summary>
internal sealed class RuntimeAssetReadBinding(IRuntimeAssetReadSource? source)
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _cancellationComplete;
    internal IRuntimeAssetReadSource? Source { get; } = source;
    internal CancellationToken Token => _lifetime.Token;
    internal volatile bool Retired;
    internal int Readers;
    internal int Publishers;

    internal void CancelRetiredReads()
    {
        try { _lifetime.Cancel(); }
        catch (AggregateException error)
        {
            System.Diagnostics.Trace.TraceError("Raw asset read cancellation callback failed: {0}", error);
        }
        finally
        {
            bool dispose;
            lock (RuntimeAssetReadServices.Gate)
            {
                _cancellationComplete = true;
                dispose = Readers == 0;
            }
            if (dispose)
                _lifetime.Dispose();
        }
    }

    internal void ReleaseReader()
    {
        bool dispose;
        lock (RuntimeAssetReadServices.Gate)
        {
            Readers--;
            dispose = Retired && _cancellationComplete && Readers == 0;
        }
        if (dispose)
            _lifetime.Dispose();
    }
}
