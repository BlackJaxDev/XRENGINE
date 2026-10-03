namespace XREngine.Rendering;

/// <summary>
/// Native-callable Dear ImGui platform and renderer viewport addresses supplied by a
/// non-collectible host assembly. Each address dispatches to the callbacks registered with
/// <see cref="RendererImGuiViewportCallbackBridge"/> for the current ImGui context.
/// </summary>
public interface IRendererImGuiViewportEntryPoints
{
    nint PlatformCreateWindow { get; }
    nint PlatformDestroyWindow { get; }
    nint PlatformShowWindow { get; }
    nint PlatformSetWindowPosition { get; }
    nint PlatformGetWindowPosition { get; }
    nint PlatformSetWindowSize { get; }
    nint PlatformGetWindowSize { get; }
    nint PlatformSetWindowFocus { get; }
    nint PlatformGetWindowFocus { get; }
    nint PlatformGetWindowMinimized { get; }
    nint PlatformSetWindowTitle { get; }
    nint PlatformSetWindowAlpha { get; }
    nint PlatformUpdateWindow { get; }
    nint PlatformRenderWindow { get; }
    nint PlatformSwapBuffers { get; }
    nint PlatformGetWindowDpiScale { get; }
    nint PlatformOnChangedViewport { get; }
    nint RendererCreateWindow { get; }
    nint RendererDestroyWindow { get; }
    nint RendererSetWindowSize { get; }
    nint RendererRenderWindow { get; }
    nint RendererSwapBuffers { get; }
    nint MonitorEnumeration { get; }
}
