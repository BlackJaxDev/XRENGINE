using Silk.NET.Maths;

namespace XREngine.Rendering;

internal sealed class GlfwResizeCallbackInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.GlfwResizeCallbackRender;

    public override void Install(XRWindow window)
    {
        base.Install(window);
        window.Window.Resize += OnWindowResize;
        Debug.Rendering(
            "[InteractiveResize] GLFW resize callback render subscribed window={0}.",
            window.GetHashCode());
    }

    public override void Uninstall()
    {
        XRWindow? window = Window;
        if (window is not null)
        {
            try
            {
                window.Window.Resize -= OnWindowResize;
            }
            catch
            {
            }
        }

        base.Uninstall();
    }

    public override void OnFramebufferResizeQueued(Vector2D<int> framebufferSize)
        => RecordCallbackAndRender("glfw-framebuffer-resize");

    private void OnWindowResize(Vector2D<int> size)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        window.QueueCurrentFramebufferResize("glfw-window-resize");
        window.InteractiveResizeDiagnostics.RecordCallback("glfw-window-resize");
        window.RenderInteractiveResizeFrame("glfw-window-resize");
    }
}
