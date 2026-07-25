using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Stable ABI used by renderer modules to service Dear ImGui platform callbacks without
/// publishing unmanaged entry points whose code belongs to a collectible assembly.
/// </summary>
public interface IRendererImGuiViewportCallbacks
{
    void PlatformCreateWindow(nint viewport);
    void PlatformDestroyWindow(nint viewport);
    void PlatformShowWindow(nint viewport);
    void PlatformSetWindowPosition(nint viewport, Vector2 value);
    void PlatformGetWindowPosition(nint viewport, nint value);
    void PlatformSetWindowSize(nint viewport, Vector2 value);
    void PlatformGetWindowSize(nint viewport, nint value);
    void PlatformSetWindowFocus(nint viewport);
    byte PlatformGetWindowFocus(nint viewport);
    byte PlatformGetWindowMinimized(nint viewport);
    void PlatformSetWindowTitle(nint viewport, nint title);
    void PlatformSetWindowAlpha(nint viewport, float alpha);
    void PlatformUpdateWindow(nint viewport);
    void PlatformRenderWindow(nint viewport, nint renderArgument);
    void PlatformSwapBuffers(nint viewport, nint renderArgument);
    float PlatformGetWindowDpiScale(nint viewport);
    void PlatformOnChangedViewport(nint viewport);
    void RendererCreateWindow(nint viewport);
    void RendererDestroyWindow(nint viewport);
    void RendererSetWindowSize(nint viewport, Vector2 value);
    void RendererRenderWindow(nint viewport, nint renderArgument);
    void RendererSwapBuffers(nint viewport, nint renderArgument);
    int EnumerateMonitor(nint monitor, nint hdc, nint rectangle);
}
