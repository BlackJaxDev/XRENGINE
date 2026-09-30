using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing.Glfw;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

internal unsafe sealed class DesktopGlfwRefreshResizeHook : IDesktopInteractiveResizeHook
{
    private DesktopSilkWindowBackend? _window;
    private Glfw? _glfw;
    private WindowHandle* _handle;
    private GlfwCallbacks.WindowRefreshCallback? _callback;
    private GlfwCallbacks.WindowRefreshCallback? _previous;

    public void Install(DesktopSilkWindowBackend window)
    {
        if (!GlfwWindowing.IsViewGlfw(window.NativeWindow))
            throw new NotSupportedException("The GLFW refresh resize hook requires a GLFW desktop window.");
        _window = window;
        _glfw = GlfwWindowing.GetExistingApi(window.NativeWindow);
        _handle = GlfwWindowing.GetHandle(window.NativeWindow);
        if (_glfw is null || _handle is null)
            throw new InvalidOperationException("The GLFW refresh resize hook could not acquire a native window handle.");
        _callback = OnRefresh;
        _previous = _glfw.SetWindowRefreshCallback(_handle, _callback);
    }

    public void OnInputCreated(IInputContext input) { }

    private void OnRefresh(WindowHandle* handle)
    {
        _previous?.Invoke(handle);
        _window?.UpdateNativeResize();
    }

    public void Dispose()
    {
        if (_glfw is not null && _handle is not null)
            _glfw.SetWindowRefreshCallback(_handle, _previous);
        _window = null;
        _glfw = null;
        _handle = null;
        _callback = null;
        _previous = null;
    }
}
