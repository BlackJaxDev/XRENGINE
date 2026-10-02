namespace XREngine.Browser;

/// <summary>Ends the synchronous integration interval after publication scopes and rollback have unwound.</summary>
internal sealed class BrowserAssetIntegrationAdmission(int session, int ticket) : IDisposable
{
    private int _ticket = ticket;
    public void Dispose()
    {
        int current = Interlocked.Exchange(ref _ticket, 0);
        if (current != 0) BrowserEngineAssetImports.FinishIntegration(session, current);
    }
}
