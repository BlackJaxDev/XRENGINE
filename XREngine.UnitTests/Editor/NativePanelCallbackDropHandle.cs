namespace XREngine.UnitTests.Editor;

internal sealed class NativePanelCallbackDropHandle(Action onDispose) : IDisposable
{
    public bool WasDisposed { get; private set; }

    public void Dispose()
    {
        WasDisposed = true;
        onDispose();
    }
}
