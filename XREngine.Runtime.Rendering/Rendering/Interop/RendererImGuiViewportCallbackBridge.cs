using ImGuiNET;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Owns process-lifetime unmanaged Dear ImGui callback entry points in the stable rendering
/// kernel and dispatches them to the module registered for the current ImGui context.
/// </summary>
public static unsafe class RendererImGuiViewportCallbackBridge
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

    public static nint PlatformCreateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformCreateWindow;
    public static nint PlatformDestroyWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformDestroyWindow;
    public static nint PlatformShowWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformShowWindow;
    public static nint PlatformSetWindowPosition => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnPlatformSetWindowPosition;
    public static nint PlatformGetWindowPosition => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformGetWindowPosition;
    public static nint PlatformSetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnPlatformSetWindowSize;
    public static nint PlatformGetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformGetWindowSize;
    public static nint PlatformSetWindowFocus => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformSetWindowFocus;
    public static nint PlatformGetWindowFocus => (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&OnPlatformGetWindowFocus;
    public static nint PlatformGetWindowMinimized => (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&OnPlatformGetWindowMinimized;
    public static nint PlatformSetWindowTitle => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformSetWindowTitle;
    public static nint PlatformSetWindowAlpha => (nint)(delegate* unmanaged[Cdecl]<nint, float, void>)&OnPlatformSetWindowAlpha;
    public static nint PlatformUpdateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformUpdateWindow;
    public static nint PlatformRenderWindow => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformRenderWindow;
    public static nint PlatformSwapBuffers => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformSwapBuffers;
    public static nint PlatformGetWindowDpiScale => (nint)(delegate* unmanaged[Cdecl]<nint, float>)&OnPlatformGetWindowDpiScale;
    public static nint PlatformOnChangedViewport => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformOnChangedViewport;
    public static nint RendererCreateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnRendererCreateWindow;
    public static nint RendererDestroyWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnRendererDestroyWindow;
    public static nint RendererSetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnRendererSetWindowSize;
    public static nint RendererRenderWindow => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnRendererRenderWindow;
    public static nint RendererSwapBuffers => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnRendererSwapBuffers;
    public static nint MonitorEnumeration => (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&OnMonitorEnumeration;

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

    private static bool TryGetCallbacks(out IRendererImGuiViewportCallbacks callbacks)
    {
        nint context = ImGui.GetCurrentContext();
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformCreateWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformCreateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformDestroyWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformDestroyWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformShowWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformShowWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowPosition(nint viewport, Vector2 value)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowPosition(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformGetWindowPosition(nint viewport, nint value)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformGetWindowPosition(viewport, value);
        else
            *(Vector2*)value = Vector2.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowSize(nint viewport, Vector2 value)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowSize(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformGetWindowSize(nint viewport, nint value)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformGetWindowSize(viewport, value);
        else
            *(Vector2*)value = Vector2.One;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowFocus(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowFocus(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPlatformGetWindowFocus(nint viewport)
        => TryGetCallbacks(out var callbacks) ? callbacks.PlatformGetWindowFocus(viewport) : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPlatformGetWindowMinimized(nint viewport)
        => TryGetCallbacks(out var callbacks) ? callbacks.PlatformGetWindowMinimized(viewport) : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowTitle(nint viewport, nint title)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowTitle(viewport, title);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowAlpha(nint viewport, float alpha)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowAlpha(viewport, alpha);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformUpdateWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformUpdateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformRenderWindow(nint viewport, nint renderArgument)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformRenderWindow(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSwapBuffers(nint viewport, nint renderArgument)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformSwapBuffers(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float OnPlatformGetWindowDpiScale(nint viewport)
        => TryGetCallbacks(out var callbacks) ? callbacks.PlatformGetWindowDpiScale(viewport) : 1.0f;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformOnChangedViewport(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.PlatformOnChangedViewport(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererCreateWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.RendererCreateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererDestroyWindow(nint viewport)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.RendererDestroyWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererSetWindowSize(nint viewport, Vector2 value)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.RendererSetWindowSize(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererRenderWindow(nint viewport, nint renderArgument)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.RendererRenderWindow(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererSwapBuffers(nint viewport, nint renderArgument)
    {
        if (TryGetCallbacks(out var callbacks))
            callbacks.RendererSwapBuffers(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitorEnumeration(nint monitor, nint hdc, nint rectangle, nint data)
        => TryGetCallbacks(out var callbacks)
            ? callbacks.EnumerateMonitor(monitor, hdc, rectangle)
            : 0;
}
