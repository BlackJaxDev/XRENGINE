namespace XREngine.Rendering;

internal sealed class SdlBackendInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.SdlBackend;

    public override void Install(XRWindow window)
    {
        base.Install(window);
        Debug.Rendering(
            "[InteractiveResize] SDL backend strategy selected for window={0}; resize rendering uses the backend-neutral framebuffer queue.",
            window.GetHashCode());
    }
}
