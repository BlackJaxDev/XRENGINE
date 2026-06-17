namespace XREngine;

internal sealed class RuntimeEngineTimer
{
    public RuntimeTimerFrame Update { get; } = new(ERuntimeTimerFrameKind.Update);
    public RuntimeTimerFrame Render { get; } = new(ERuntimeTimerFrameKind.Render);
    public event Action? UpdateFrame;
    public event Action? CollectVisible;
    public event Action? SwapBuffers;

    public void RaiseUpdateFrame() => UpdateFrame?.Invoke();
    public void RaiseCollectVisible() => CollectVisible?.Invoke();
    public void RaiseSwapBuffers() => SwapBuffers?.Invoke();
}
