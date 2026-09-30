namespace XREngine.Rendering;

/// <summary>Restores absent presentation without disturbing a subsequently installed host.</summary>
internal sealed class RuntimePresentationlessInstallationScope(
    IRuntimeRenderPresentationServices installed, IRuntimeRenderWorkServices work) : IDisposable
{
    private IRuntimeRenderPresentationServices? _installed = installed;

    public void Dispose()
    {
        IRuntimeRenderPresentationServices? expected = Interlocked.Exchange(ref _installed, null);
        if (expected is not null)
            RuntimeRenderingHostServices.RestorePresentationless(expected, work);
    }
}
