namespace XREngine.Rendering;

/// <summary>Focused application boundary for window-pump policy and mailbox routing.</summary>
public interface IRuntimeWindowApplicationServices
{
    bool IsRunning { get; }
    WindowMailboxDiagnostics Diagnostics { get; }
    bool TryStartForStartupWindows(IReadOnlyList<WindowStartupValues> windows);
    bool ShouldCreateWindowOnHost(WindowStartupValues window);
    XRWindow CreateWindow(Func<XRWindow> factory, string reason);
    void UnregisterWindow(XRWindow window);
    void EnqueueWindowTask(IRuntimeRenderWindowHost window, Action task, string reason);
    T InvokeWindowTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason);
    void Stop();
}

/// <summary>Current Bootstrap-installed window application capability.</summary>
public static class RuntimeWindowApplicationServices
{
    private static IRuntimeWindowApplicationServices _current = new DirectWindowApplicationServices();

    public static IRuntimeWindowApplicationServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    private sealed class DirectWindowApplicationServices : IRuntimeWindowApplicationServices
    {
        public bool IsRunning => false;
        public WindowMailboxDiagnostics Diagnostics => default;
        public bool TryStartForStartupWindows(IReadOnlyList<WindowStartupValues> windows) => false;
        public bool ShouldCreateWindowOnHost(WindowStartupValues window) => false;
        public XRWindow CreateWindow(Func<XRWindow> factory, string reason) => factory();
        public void UnregisterWindow(XRWindow window) { }
        public void EnqueueWindowTask(IRuntimeRenderWindowHost window, Action task, string reason) => task();
        public T InvokeWindowTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason) => task();
        public void Stop() { }
    }
}
