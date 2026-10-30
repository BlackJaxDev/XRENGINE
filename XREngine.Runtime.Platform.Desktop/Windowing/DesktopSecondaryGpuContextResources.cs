using XREngine.Data.Vectors;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Owns the secondary native window and its renderer on their declared threads.</summary>
internal sealed class DesktopSecondaryGpuContextResources : IDesktopSecondaryGpuContextResources
{
    private readonly XRWindow _window;
    private readonly AbstractRenderer _renderer;
    private readonly DesktopSecondaryGpuContextRelease _release;
    private bool _nativeReleaseFailed;

    internal DesktopSecondaryGpuContextResources(
        IRuntimeWindowBackendFactory factory, XRWindow template, bool allowContextSharing)
    {
        if (Environment.CurrentManagedThreadId != template.NativeWindowThreadId)
            throw new InvalidOperationException("Secondary GPU context creation requires the native window owner thread.");
        IRuntimeWindowGlContext templateContext = template.DesktopGlContext
            ?? throw new NotSupportedException("A secondary GPU context requires a desktop OpenGL context.");
        IRuntimeWindowApplicationServices windowApplication = RuntimeWindowApplicationServices.Current;
        IRuntimeRenderSchedulingServices scheduling = RuntimeRenderingHostServices.Scheduling;
        var size = template.WindowSizeSnapshot;
        int width = Math.Max(64, size.X / 8);
        int height = Math.Max(64, size.Y / 8);
        var startup = WindowStartupValues.Default with
        {
            Title = "XR Secondary GPU Context",
            Width = width,
            Height = height,
        };
        var request = new RuntimeWindowCreateOptions(
            startup, RuntimeGraphicsApiKind.OpenGL, EInteractiveWindowResizeStrategy.Default,
            RuntimeWindowPurpose.SecondaryGpuContext, IVector2.Zero, new IVector2(width, height),
            false, false, false, false, false, 32, 24, 8, 4, 6, false, false, false,
            allowContextSharing ? templateContext : null);
        XRWindow? window = null;
        try
        {
            window = XRWindow.CreateSecondaryGpuContextWindow(request, factory);
            IRuntimeWindowGlContext context = window.DesktopGlContext
                ?? throw new InvalidOperationException("Secondary GPU window has no GL context.");
            context.ClearCurrent();
            templateContext.MakeCurrent();
            _window = window;
            _renderer = window.Renderer;
            _release = new DesktopSecondaryGpuContextRelease(
                template.NativeWindowThreadId, template.RenderOwnerThreadId,
                task => scheduling.EnqueueRenderThreadTask(task, "SecondaryGpuContext.DestroyNativeWindow"),
                task => windowApplication.EnqueueWindowTask(template, task, "SecondaryGpuContext.DestroyNativeWindow"),
                TryDisposeNativeWindow);
        }
        catch
        {
            try { window?.Dispose(); }
            finally { templateContext.MakeCurrent(); }
            throw;
        }
    }

    public bool HasRenderer => true;

    public void Initialize()
    {
        IRuntimeWindowGlContext context = _window.DesktopGlContext
            ?? throw new InvalidOperationException("Secondary GPU window lost its GL context.");
        context.MakeCurrent();
        _renderer.Initialize();
    }

    public void Execute(Action<AbstractRenderer> job) => _window.ExecuteSecondaryGpuJob(job);

    public void CleanUp()
    {
        try
        {
            _renderer.CleanUp();
            _window.DesktopGlContext?.ClearCurrent();
        }
        catch
        {
            _window.AbandonSecondaryContextTeardown();
            throw;
        }
    }

    public void Release(Action<bool> completed) => _release.Release(completed);

    private bool TryDisposeNativeWindow()
    {
        // XRWindow can mark itself disposed in its finally path after a failure.
        // A failed native release must remain retained even when a later Dispose is a no-op.
        if (_nativeReleaseFailed)
            return false;
        try
        {
            if (_window.TryDisposeSecondaryGpuContextWindow())
                return true;
            _nativeReleaseFailed = true;
            _window.AbandonSecondaryContextTeardown();
            try { Debug.RenderingWarning("Secondary GPU native window disposal did not complete; ownership remains retained."); }
            catch { }
        }
        catch (Exception error)
        {
            _nativeReleaseFailed = true;
            _window.AbandonSecondaryContextTeardown();
            try { Debug.RenderingWarning($"Secondary GPU native window disposal failed: {error.Message}"); }
            catch { }
        }
        return false;
    }
}
