using Silk.NET.Input;
using Silk.NET.Maths;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

internal sealed class DesktopGlfwResizeHook : IDesktopInteractiveResizeHook
{
    private DesktopSilkWindowBackend? _window;

    public void Install(DesktopSilkWindowBackend window)
    {
        _window = window;
        window.NativeWindow.Resize += OnResize;
    }

    public void OnInputCreated(IInputContext input) { }

    private void OnResize(Vector2D<int> _) => _window?.UpdateNativeResize();

    public void Dispose()
    {
        if (_window is { } window)
            window.NativeWindow.Resize -= OnResize;
        _window = null;
    }
}
