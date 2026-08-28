using XREngine.Rendering;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Bootstrap policy for selecting and owning Rendering's optional window pump.</summary>
internal sealed class EngineRuntimeWindowApplicationServices : IRuntimeWindowApplicationServices, IDisposable
{
    private readonly RuntimeWindowPumpHost _host = new();

    public bool IsRunning => _host.IsRunning;
    public WindowMailboxDiagnostics Diagnostics => _host.Diagnostics;

    public bool TryStartForStartupWindows(IReadOnlyList<WindowStartupValues> windows)
    {
        RuntimeWindowPumpHostMode requestedMode = ResolveRequestedMode();
        if (requestedMode == RuntimeWindowPumpHostMode.Disabled || !CanUse(windows))
            return false;
        _host.Start(requestedMode);
        return true;
    }

    public bool ShouldCreateWindowOnHost(WindowStartupValues window)
        => _host.IsRunning
            && _host.Mode == RuntimeWindowPumpHostMode.SdlPrototype
            && window.InteractiveResizeMode == RuntimeWindowResizeMode.NativeBackend
            && Engine.EffectiveSettings.PreferredRenderBackend == ERenderLibrary.Vulkan;

    public XRWindow CreateWindow(Func<XRWindow> factory, string reason) => _host.CreateWindow(factory, reason);
    public void UnregisterWindow(XRWindow window) => _host.UnregisterWindow(window);
    public void EnqueueWindowTask(IRuntimeRenderWindowHost window, Action task, string reason)
        => _host.EnqueueWindowTask(window, task, reason);
    public T InvokeWindowTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason)
        => _host.InvokeWindowTask(window, task, reason);
    public void Stop() => _host.Stop();
    public void Dispose() => _host.Dispose();

    private static RuntimeWindowPumpHostMode ResolveRequestedMode()
    {
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.WindowPumpHost)?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            return RuntimeWindowPumpHostMode.Disabled;
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("sdl", StringComparison.OrdinalIgnoreCase)
            || value.Equals("sdl-prototype", StringComparison.OrdinalIgnoreCase))
            return RuntimeWindowPumpHostMode.SdlPrototype;

        Debug.RenderingWarning(
            "[WindowPumpHost] Ignoring invalid {0}='{1}'. Expected off or sdl-prototype.",
            XREngineEnvironmentVariables.WindowPumpHost,
            value);
        return RuntimeWindowPumpHostMode.Disabled;
    }

    private static bool CanUse(IReadOnlyList<WindowStartupValues> windows)
    {
        if (!OperatingSystem.IsWindows())
        {
            Debug.RenderingWarning("[WindowPumpHost] SDL prototype pump is currently Windows-only.");
            return false;
        }
        if (Engine.EffectiveSettings.PreferredRenderBackend != ERenderLibrary.Vulkan)
        {
            Debug.RenderingWarning(
                "[WindowPumpHost] SDL prototype pump requires Vulkan. Current render backend={0}.",
                Engine.EffectiveSettings.PreferredRenderBackend);
            return false;
        }
        if (windows.Count == 0)
            return false;
        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i].InteractiveResizeMode == RuntimeWindowResizeMode.NativeBackend)
                continue;
            Debug.RenderingWarning(
                "[WindowPumpHost] SDL prototype requires NativeBackend resize mode for every startup window. Window='{0}'.",
                windows[i].Title ?? string.Empty);
            return false;
        }
        return true;
    }
}
