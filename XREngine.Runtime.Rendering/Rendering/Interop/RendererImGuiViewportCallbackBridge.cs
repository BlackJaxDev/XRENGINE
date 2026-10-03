namespace XREngine.Rendering;

/// <summary>
/// Routes Dear ImGui viewport callbacks to the renderer module registered for the current ImGui
/// context. The native-callable addresses come from the host through <see cref="EntryPoints"/>,
/// so they outlive every collectible renderer generation.
/// </summary>
public static class RendererImGuiViewportCallbackBridge
{
    private sealed record Registration(long Id, IRendererImGuiViewportCallbacks Callbacks);

    private sealed class RegistrationLease(nint context, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (Sync)
            {
                if (Registrations.TryGetValue(context, out Registration? registration) &&
                    registration.Id == id)
                {
                    Registrations.Remove(context);
                }
            }
        }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<nint, Registration> Registrations = [];
    private static long _nextRegistrationId;
    private static IRendererImGuiViewportEntryPoints? _entryPoints;

    /// <summary>The host-installed native-callable addresses, or null when no native host is composed.</summary>
    public static IRendererImGuiViewportEntryPoints? EntryPoints
    {
        get => Volatile.Read(ref _entryPoints);
        set => Volatile.Write(ref _entryPoints, value);
    }

    private static IRendererImGuiViewportEntryPoints RequiredEntryPoints => EntryPoints ??
        throw new InvalidOperationException(
            "Dear ImGui viewport entry points are not installed. Install a desktop platform backend before enabling ImGui viewports.");

    public static nint PlatformCreateWindow => RequiredEntryPoints.PlatformCreateWindow;
    public static nint PlatformDestroyWindow => RequiredEntryPoints.PlatformDestroyWindow;
    public static nint PlatformShowWindow => RequiredEntryPoints.PlatformShowWindow;
    public static nint PlatformSetWindowPosition => RequiredEntryPoints.PlatformSetWindowPosition;
    public static nint PlatformGetWindowPosition => RequiredEntryPoints.PlatformGetWindowPosition;
    public static nint PlatformSetWindowSize => RequiredEntryPoints.PlatformSetWindowSize;
    public static nint PlatformGetWindowSize => RequiredEntryPoints.PlatformGetWindowSize;
    public static nint PlatformSetWindowFocus => RequiredEntryPoints.PlatformSetWindowFocus;
    public static nint PlatformGetWindowFocus => RequiredEntryPoints.PlatformGetWindowFocus;
    public static nint PlatformGetWindowMinimized => RequiredEntryPoints.PlatformGetWindowMinimized;
    public static nint PlatformSetWindowTitle => RequiredEntryPoints.PlatformSetWindowTitle;
    public static nint PlatformSetWindowAlpha => RequiredEntryPoints.PlatformSetWindowAlpha;
    public static nint PlatformUpdateWindow => RequiredEntryPoints.PlatformUpdateWindow;
    public static nint PlatformRenderWindow => RequiredEntryPoints.PlatformRenderWindow;
    public static nint PlatformSwapBuffers => RequiredEntryPoints.PlatformSwapBuffers;
    public static nint PlatformGetWindowDpiScale => RequiredEntryPoints.PlatformGetWindowDpiScale;
    public static nint PlatformOnChangedViewport => RequiredEntryPoints.PlatformOnChangedViewport;
    public static nint RendererCreateWindow => RequiredEntryPoints.RendererCreateWindow;
    public static nint RendererDestroyWindow => RequiredEntryPoints.RendererDestroyWindow;
    public static nint RendererSetWindowSize => RequiredEntryPoints.RendererSetWindowSize;
    public static nint RendererRenderWindow => RequiredEntryPoints.RendererRenderWindow;
    public static nint RendererSwapBuffers => RequiredEntryPoints.RendererSwapBuffers;
    public static nint MonitorEnumeration => RequiredEntryPoints.MonitorEnumeration;

    public static IDisposable Register(nint context, IRendererImGuiViewportCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        if (context == 0)
            throw new ArgumentException("An initialized ImGui context is required.", nameof(context));

        long id = Interlocked.Increment(ref _nextRegistrationId);
        lock (Sync)
            Registrations[context] = new(id, callbacks);
        return new RegistrationLease(context, id);
    }

    /// <summary>Resolves the callbacks registered for the current ImGui context.</summary>
    public static bool TryGetCallbacks(out IRendererImGuiViewportCallbacks callbacks)
    {
        nint context = UI.ImGuiRuntimeServices.Current?.CurrentContext ?? 0;
        lock (Sync)
        {
            if (Registrations.TryGetValue(context, out Registration? registration))
            {
                callbacks = registration.Callbacks;
                return true;
            }
        }

        callbacks = null!;
        return false;
    }
}
