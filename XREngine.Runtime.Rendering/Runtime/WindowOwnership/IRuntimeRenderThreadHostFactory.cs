namespace XREngine.Rendering;

/// <summary>Creates a native render-loop owner through the installed window backend.</summary>
public interface IRuntimeRenderThreadHostFactory
{
    IRuntimeRenderThreadHost CreateRenderThreadHost(
        Action<Func<bool>> blockForTimerRendering, Action waitToRender, Action stopTimer);
}
