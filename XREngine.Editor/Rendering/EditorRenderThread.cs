using XREngine.Rendering;

namespace XREngine.Editor.Services;

internal static class EditorRenderThread
{
    public static T Invoke<T>(
        Func<T> task,
        string reason,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.RequiresGraphicsContext)
        => RuntimeRenderingHostServices.Scheduling.InvokeRenderThreadTask(task, reason, renderThreadKind);
}
