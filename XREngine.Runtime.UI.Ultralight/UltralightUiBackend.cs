using XREngine.Rendering.UI;

namespace XREngine.Runtime.UI.Ultralight;

/// <summary>Installs the optional desktop software web-rendering backend.</summary>
public static class UltralightUiBackend
{
    public static void Register()
        => WebRendererBackendRegistry.RegisterSoftware(static () => new UltralightWebRendererBackend());
}
