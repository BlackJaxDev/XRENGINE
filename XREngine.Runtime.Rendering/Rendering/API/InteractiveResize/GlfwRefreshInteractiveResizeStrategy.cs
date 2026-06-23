using Silk.NET.GLFW;
using Silk.NET.Windowing.Glfw;

namespace XREngine.Rendering;

internal unsafe sealed class GlfwRefreshInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    private Glfw? _glfw;
    private WindowHandle* _handle;
    private GlfwCallbacks.WindowRefreshCallback? _callback;
    private GlfwCallbacks.WindowRefreshCallback? _previousCallback;

    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.GlfwRefreshCallback;

    public override void Install(XRWindow window)
    {
        base.Install(window);

        if (!GlfwWindowing.IsViewGlfw(window.Window))
        {
            Debug.RenderingWarning(
                "[InteractiveResize] GLFW refresh strategy requested for non-GLFW window={0}; no callback installed. Backend={1}",
                window.GetHashCode(),
                window.ActualWindowingBackendName);
            return;
        }

        try
        {
            _glfw = GlfwWindowing.GetExistingApi(window.Window);
            _handle = GlfwWindowing.GetHandle(window.Window);
            if (_glfw is null || _handle is null)
            {
                Debug.RenderingWarning(
                    "[InteractiveResize] GLFW refresh strategy could not resolve native handle for window={0}.",
                    window.GetHashCode());
                return;
            }

            _callback = OnWindowRefresh;
            _previousCallback = _glfw.SetWindowRefreshCallback(_handle, _callback);

            Debug.Rendering(
                "[InteractiveResize] GLFW refresh callback installed window={0} handle=0x{1:X}.",
                window.GetHashCode(),
                (nuint)_handle);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Failed to install GLFW refresh callback for window={0}. {1}",
                window.GetHashCode(),
                ex);
        }
    }

    public override void Uninstall()
    {
        XRWindow? window = Window;
        try
        {
            if (_glfw is not null && _handle is not null)
            {
                _glfw.SetWindowRefreshCallback(_handle, _previousCallback);
                Debug.Rendering(
                    "[InteractiveResize] GLFW refresh callback restored window={0} handle=0x{1:X}.",
                    window?.GetHashCode() ?? 0,
                    (nuint)_handle);
            }
        }
        catch (Exception ex)
        {
            Debug.RenderingWarning(
                "[InteractiveResize] Failed to restore GLFW refresh callback for window={0}. {1}",
                window?.GetHashCode() ?? 0,
                ex);
        }
        finally
        {
            _previousCallback = null;
            _callback = null;
            _handle = null;
            _glfw = null;
            base.Uninstall();
        }
    }

    private void OnWindowRefresh(WindowHandle* handle)
    {
        try
        {
            if (_previousCallback is not null)
                _previousCallback(handle);
        }
        catch (Exception ex)
        {
            Debug.RenderingWarningEvery(
                $"InteractiveResize.GlfwRefresh.Previous.{Window?.GetHashCode() ?? 0}",
                TimeSpan.FromSeconds(1),
                "[InteractiveResize] Previous GLFW refresh callback failed for window={0}. {1}",
                Window?.GetHashCode() ?? 0,
                ex.Message);
        }

        RecordCallbackAndRender("glfw-refresh");
    }
}
