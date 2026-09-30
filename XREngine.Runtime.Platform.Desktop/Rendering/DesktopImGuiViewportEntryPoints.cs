using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Rendering;

/// <summary>
/// Process-lifetime unmanaged Dear ImGui viewport entry points. They live in this
/// non-collectible assembly and dispatch to the renderer module registered for the current
/// ImGui context, so ImGui never holds a code address from an unloadable renderer generation.
/// </summary>
internal sealed unsafe class DesktopImGuiViewportEntryPoints : IRendererImGuiViewportEntryPoints
{
    public nint PlatformCreateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformCreateWindow;
    public nint PlatformDestroyWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformDestroyWindow;
    public nint PlatformShowWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformShowWindow;
    public nint PlatformSetWindowPosition => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnPlatformSetWindowPosition;
    public nint PlatformGetWindowPosition => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformGetWindowPosition;
    public nint PlatformSetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnPlatformSetWindowSize;
    public nint PlatformGetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformGetWindowSize;
    public nint PlatformSetWindowFocus => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformSetWindowFocus;
    public nint PlatformGetWindowFocus => (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&OnPlatformGetWindowFocus;
    public nint PlatformGetWindowMinimized => (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&OnPlatformGetWindowMinimized;
    public nint PlatformSetWindowTitle => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformSetWindowTitle;
    public nint PlatformSetWindowAlpha => (nint)(delegate* unmanaged[Cdecl]<nint, float, void>)&OnPlatformSetWindowAlpha;
    public nint PlatformUpdateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformUpdateWindow;
    public nint PlatformRenderWindow => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformRenderWindow;
    public nint PlatformSwapBuffers => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPlatformSwapBuffers;
    public nint PlatformGetWindowDpiScale => (nint)(delegate* unmanaged[Cdecl]<nint, float>)&OnPlatformGetWindowDpiScale;
    public nint PlatformOnChangedViewport => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlatformOnChangedViewport;
    public nint RendererCreateWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnRendererCreateWindow;
    public nint RendererDestroyWindow => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnRendererDestroyWindow;
    public nint RendererSetWindowSize => (nint)(delegate* unmanaged[Cdecl]<nint, Vector2, void>)&OnRendererSetWindowSize;
    public nint RendererRenderWindow => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnRendererRenderWindow;
    public nint RendererSwapBuffers => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnRendererSwapBuffers;
    public nint MonitorEnumeration => (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)&OnMonitorEnumeration;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformCreateWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformCreateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformDestroyWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformDestroyWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformShowWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformShowWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowPosition(nint viewport, Vector2 value)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowPosition(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformGetWindowPosition(nint viewport, nint value)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformGetWindowPosition(viewport, value);
        else
            *(Vector2*)value = Vector2.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowSize(nint viewport, Vector2 value)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowSize(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformGetWindowSize(nint viewport, nint value)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformGetWindowSize(viewport, value);
        else
            *(Vector2*)value = Vector2.One;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowFocus(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowFocus(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPlatformGetWindowFocus(nint viewport)
        => RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks)
            ? callbacks.PlatformGetWindowFocus(viewport)
            : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPlatformGetWindowMinimized(nint viewport)
        => RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks)
            ? callbacks.PlatformGetWindowMinimized(viewport)
            : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowTitle(nint viewport, nint title)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowTitle(viewport, title);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSetWindowAlpha(nint viewport, float alpha)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSetWindowAlpha(viewport, alpha);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformUpdateWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformUpdateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformRenderWindow(nint viewport, nint renderArgument)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformRenderWindow(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformSwapBuffers(nint viewport, nint renderArgument)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformSwapBuffers(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static float OnPlatformGetWindowDpiScale(nint viewport)
        => RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks)
            ? callbacks.PlatformGetWindowDpiScale(viewport)
            : 1.0f;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlatformOnChangedViewport(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.PlatformOnChangedViewport(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererCreateWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.RendererCreateWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererDestroyWindow(nint viewport)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.RendererDestroyWindow(viewport);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererSetWindowSize(nint viewport, Vector2 value)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.RendererSetWindowSize(viewport, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererRenderWindow(nint viewport, nint renderArgument)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.RendererRenderWindow(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRendererSwapBuffers(nint viewport, nint renderArgument)
    {
        if (RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks))
            callbacks.RendererSwapBuffers(viewport, renderArgument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitorEnumeration(nint monitor, nint hdc, nint rectangle, nint data)
        => RendererImGuiViewportCallbackBridge.TryGetCallbacks(out var callbacks)
            ? callbacks.EnumerateMonitor(monitor, hdc, rectangle)
            : 0;
}
