namespace XREngine.Browser;

/// <summary>Accounts an explicitly scoped managed companion buffer until its parser finishes.</summary>
internal sealed class BrowserAssetStagingLease : IDisposable
{
    private readonly int _session;
    private int _bytes;
    public BrowserAssetStagingLease(int session, int bytes)
    {
        _session = session;
        BrowserEngineAssetImports.AdjustManagedStaging(session, bytes);
        _bytes = bytes;
    }
    public void Dispose()
    {
        int bytes = Interlocked.Exchange(ref _bytes, 0);
        if (bytes != 0) BrowserEngineAssetImports.AdjustManagedStaging(_session, -bytes);
    }
}
