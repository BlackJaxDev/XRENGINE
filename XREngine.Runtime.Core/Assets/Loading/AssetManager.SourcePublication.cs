namespace XREngine;

public partial class AssetManager
{
    private int _sourcePublications;

    /// <summary>Retains a cache epoch through a short synchronous source commit without holding its gate.</summary>
    public IDisposable BeginSourcePublication(int expectedEpoch)
    {
        lock (_runtimePublicationGate)
        {
            if (expectedEpoch != RuntimeSourceEpoch || _runtimeSourceTeardown || _runtimeSourceUnbinding ||
                _runtimeSourceDisposing || _remoteAssetDisposalInProgress)
                throw new OperationCanceledException("AssetSource.StalePublication: retry source preparation against the current asset owner.");
            _sourcePublications++;
        }
        return new SourcePublicationLease(this);
    }

    private sealed class SourcePublicationLease(AssetManager owner) : IDisposable
    {
        private AssetManager? _owner = owner;

        public void Dispose()
        {
            AssetManager? current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._runtimePublicationGate) current._sourcePublications--;
        }
    }
}
