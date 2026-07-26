using ImGuiNET;
using System.Numerics;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private sealed unsafe partial class OpenGLImGuiMultiViewportController : IRendererImGuiViewportCallbacks
    {
        void IRendererImGuiViewportCallbacks.PlatformCreateWindow(nint viewport)
            => PlatformCreateWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformDestroyWindow(nint viewport)
            => PlatformDestroyWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformShowWindow(nint viewport)
            => PlatformShowWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformSetWindowPosition(nint viewport, Vector2 value)
            => PlatformSetWindowPos((ImGuiViewport*)viewport, value);

        void IRendererImGuiViewportCallbacks.PlatformGetWindowPosition(nint viewport, nint value)
            => PlatformGetWindowPos((ImGuiViewport*)viewport, (Vector2*)value);

        void IRendererImGuiViewportCallbacks.PlatformSetWindowSize(nint viewport, Vector2 value)
            => PlatformSetWindowSize((ImGuiViewport*)viewport, value);

        void IRendererImGuiViewportCallbacks.PlatformGetWindowSize(nint viewport, nint value)
            => PlatformGetWindowSize((ImGuiViewport*)viewport, (Vector2*)value);

        void IRendererImGuiViewportCallbacks.PlatformSetWindowFocus(nint viewport)
            => PlatformSetWindowFocus((ImGuiViewport*)viewport);

        byte IRendererImGuiViewportCallbacks.PlatformGetWindowFocus(nint viewport)
            => PlatformGetWindowFocus((ImGuiViewport*)viewport);

        byte IRendererImGuiViewportCallbacks.PlatformGetWindowMinimized(nint viewport)
            => PlatformGetWindowMinimized((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformSetWindowTitle(nint viewport, nint title)
            => PlatformSetWindowTitle((ImGuiViewport*)viewport, (byte*)title);

        void IRendererImGuiViewportCallbacks.PlatformSetWindowAlpha(nint viewport, float alpha)
            => PlatformSetWindowAlpha((ImGuiViewport*)viewport, alpha);

        void IRendererImGuiViewportCallbacks.PlatformUpdateWindow(nint viewport)
            => PlatformUpdateWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformRenderWindow(nint viewport, nint renderArgument)
            => PlatformRenderWindow((ImGuiViewport*)viewport, (void*)renderArgument);

        void IRendererImGuiViewportCallbacks.PlatformSwapBuffers(nint viewport, nint renderArgument)
            => PlatformSwapBuffers((ImGuiViewport*)viewport, (void*)renderArgument);

        float IRendererImGuiViewportCallbacks.PlatformGetWindowDpiScale(nint viewport)
            => PlatformGetWindowDpiScale((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.PlatformOnChangedViewport(nint viewport)
            => PlatformOnChangedViewport((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.RendererCreateWindow(nint viewport)
            => RendererCreateWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.RendererDestroyWindow(nint viewport)
            => RendererDestroyWindow((ImGuiViewport*)viewport);

        void IRendererImGuiViewportCallbacks.RendererSetWindowSize(nint viewport, Vector2 value)
            => RendererSetWindowSize((ImGuiViewport*)viewport, value);

        void IRendererImGuiViewportCallbacks.RendererRenderWindow(nint viewport, nint renderArgument)
            => RendererRenderWindow((ImGuiViewport*)viewport, (void*)renderArgument);

        void IRendererImGuiViewportCallbacks.RendererSwapBuffers(nint viewport, nint renderArgument)
            => RendererSwapBuffers((ImGuiViewport*)viewport, (void*)renderArgument);

        int IRendererImGuiViewportCallbacks.EnumerateMonitor(nint monitor, nint hdc, nint rectangle)
            => EnumMonitor(monitor, hdc, ref *(NativeRect*)rectangle, nint.Zero) ? 1 : 0;
    }
}
