namespace XREngine.Rendering;

internal sealed class DefaultInteractiveResizeStrategy : InteractiveResizeStrategyBase
{
    public override EInteractiveWindowResizeStrategy Kind => EInteractiveWindowResizeStrategy.Default;

    public override void Install(XRWindow window)
    {
        base.Install(window);
        Debug.Rendering(
            "[InteractiveResize] Default strategy leaves Silk.NET backend behavior unchanged for window={0}.",
            window.GetHashCode());
    }
}
