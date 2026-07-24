using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace XREngine.Rendering;

internal abstract class InteractiveResizeStrategyBase : IInteractiveResizeStrategy
{
    protected XRWindow? Window { get; private set; }

    public abstract EInteractiveWindowResizeStrategy Kind { get; }

    public bool IsInstalled { get; private set; }

    public virtual void Install(XRWindow window)
    {
        Window = window;
        IsInstalled = true;
        Debug.Rendering(
            "[InteractiveResize] Installed strategy={0} window={1}.",
            Kind,
            window.GetHashCode());
    }

    public virtual void Uninstall()
    {
        if (Window is not null)
        {
            Debug.Rendering(
                "[InteractiveResize] Uninstalled strategy={0} window={1}.",
                Kind,
                Window.GetHashCode());
        }

        Window = null;
        IsInstalled = false;
    }

    public virtual void OnFramebufferResizeQueued(Vector2D<int> framebufferSize)
    {
    }

    public virtual void OnInputCreated(IInputContext input)
    {
    }

    protected void RecordCallbackAndRender(string reason)
    {
        XRWindow? window = Window;
        if (window is null)
            return;

        window.InteractiveResizeDiagnostics.RecordCallback(reason);
        window.RenderInteractiveResizeFrame(reason);
    }

    protected static Vector2D<int> GetCurrentFramebufferSize(IWindow window)
    {
        Vector2D<int> framebufferSize = window.FramebufferSize;
        if (framebufferSize.X > 0 && framebufferSize.Y > 0)
            return framebufferSize;

        Vector2D<int> windowSize = window.Size;
        return new Vector2D<int>(Math.Max(1, windowSize.X), Math.Max(1, windowSize.Y));
    }
}
