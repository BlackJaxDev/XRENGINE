using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal sealed class RendererBackendTestLifecycle : IRendererBackendLifecycle
{
    public int RegisteredCount { get; private set; }

    public int UnregisteredCount { get; private set; }

    public void OnRegistered()
        => RegisteredCount++;

    public void OnUnregistered()
        => UnregisteredCount++;
}
