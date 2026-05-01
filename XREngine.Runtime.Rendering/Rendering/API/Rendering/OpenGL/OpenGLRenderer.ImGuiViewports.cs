using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer
    {
        private sealed unsafe class OpenGLImGuiMultiViewportController : IDisposable
        {
            private delegate void RenderImDrawDataDelegate(ImGuiController controller, ImDrawDataPtr drawData);
            private delegate ImGuiKey TranslateInputKeyDelegate(Key key);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportCallback(ImGuiViewport* viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportSetVec2Callback(ImGuiViewport* viewport, Vector2 value);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportGetVec2Callback(ImGuiViewport* viewport, Vector2* value);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate byte ViewportGetBoolCallback(ImGuiViewport* viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportSetTitleCallback(ImGuiViewport* viewport, byte* title);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportSetFloatCallback(ImGuiViewport* viewport, float value);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate float ViewportGetFloatCallback(ImGuiViewport* viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ViewportRenderCallback(ImGuiViewport* viewport, void* renderArg);

            private static readonly RenderImDrawDataDelegate? RenderImDrawData = CreateRenderImDrawDataDelegate();
            private static readonly TranslateInputKeyDelegate? TranslateInputKey = CreateTranslateInputKeyDelegate();

            private readonly OpenGLRenderer _renderer;
            private readonly ImGuiController _controller;
            private readonly IWindow _mainWindow;
            private readonly Dictionary<uint, PlatformWindow> _platformWindows = [];
            private readonly List<ImGuiPlatformMonitor> _monitorScratch = [];

            private readonly ViewportCallback _platformCreateWindow;
            private readonly ViewportCallback _platformDestroyWindow;
            private readonly ViewportCallback _platformShowWindow;
            private readonly ViewportSetVec2Callback _platformSetWindowPos;
            private readonly ViewportGetVec2Callback _platformGetWindowPos;
            private readonly ViewportSetVec2Callback _platformSetWindowSize;
            private readonly ViewportGetVec2Callback _platformGetWindowSize;
            private readonly ViewportCallback _platformSetWindowFocus;
            private readonly ViewportGetBoolCallback _platformGetWindowFocus;
            private readonly ViewportGetBoolCallback _platformGetWindowMinimized;
            private readonly ViewportSetTitleCallback _platformSetWindowTitle;
            private readonly ViewportSetFloatCallback _platformSetWindowAlpha;
            private readonly ViewportCallback _platformUpdateWindow;
            private readonly ViewportRenderCallback _platformRenderWindow;
            private readonly ViewportRenderCallback _platformSwapBuffers;
            private readonly ViewportGetFloatCallback _platformGetWindowDpiScale;
            private readonly ViewportCallback _platformOnChangedViewport;
            private readonly ViewportCallback _rendererCreateWindow;
            private readonly ViewportCallback _rendererDestroyWindow;
            private readonly ViewportSetVec2Callback _rendererSetWindowSize;
            private readonly ViewportRenderCallback _rendererRenderWindow;
            private readonly ViewportRenderCallback _rendererSwapBuffers;
            private readonly MonitorEnumProc _monitorEnumProc;

            private readonly nint _platformCreateWindowPtr;
            private readonly nint _platformDestroyWindowPtr;
            private readonly nint _platformShowWindowPtr;
            private readonly nint _platformSetWindowPosPtr;
            private readonly nint _platformGetWindowPosPtr;
            private readonly nint _platformSetWindowSizePtr;
            private readonly nint _platformGetWindowSizePtr;
            private readonly nint _platformSetWindowFocusPtr;
            private readonly nint _platformGetWindowFocusPtr;
            private readonly nint _platformGetWindowMinimizedPtr;
            private readonly nint _platformSetWindowTitlePtr;
            private readonly nint _platformSetWindowAlphaPtr;
            private readonly nint _platformUpdateWindowPtr;
            private readonly nint _platformRenderWindowPtr;
            private readonly nint _platformSwapBuffersPtr;
            private readonly nint _platformGetWindowDpiScalePtr;
            private readonly nint _platformOnChangedViewportPtr;
            private readonly nint _rendererCreateWindowPtr;
            private readonly nint _rendererDestroyWindowPtr;
            private readonly nint _rendererSetWindowSizePtr;
            private readonly nint _rendererRenderWindowPtr;
            private readonly nint _rendererSwapBuffersPtr;

            private bool _installed;
            private bool _disposed;
            private nint _monitorData;
            private int _monitorCapacity;

            private OpenGLImGuiMultiViewportController(OpenGLRenderer renderer, ImGuiController controller)
            {
                _renderer = renderer;
                _controller = controller;
                _mainWindow = renderer.XRWindow.Window;

                _platformCreateWindow = PlatformCreateWindow;
                _platformDestroyWindow = PlatformDestroyWindow;
                _platformShowWindow = PlatformShowWindow;
                _platformSetWindowPos = PlatformSetWindowPos;
                _platformGetWindowPos = PlatformGetWindowPos;
                _platformSetWindowSize = PlatformSetWindowSize;
                _platformGetWindowSize = PlatformGetWindowSize;
                _platformSetWindowFocus = PlatformSetWindowFocus;
                _platformGetWindowFocus = PlatformGetWindowFocus;
                _platformGetWindowMinimized = PlatformGetWindowMinimized;
                _platformSetWindowTitle = PlatformSetWindowTitle;
                _platformSetWindowAlpha = PlatformSetWindowAlpha;
                _platformUpdateWindow = PlatformUpdateWindow;
                _platformRenderWindow = PlatformRenderWindow;
                _platformSwapBuffers = PlatformSwapBuffers;
                _platformGetWindowDpiScale = PlatformGetWindowDpiScale;
                _platformOnChangedViewport = PlatformOnChangedViewport;
                _rendererCreateWindow = RendererCreateWindow;
                _rendererDestroyWindow = RendererDestroyWindow;
                _rendererSetWindowSize = RendererSetWindowSize;
                _rendererRenderWindow = RendererRenderWindow;
                _rendererSwapBuffers = RendererSwapBuffers;
                _monitorEnumProc = EnumMonitor;

                _platformCreateWindowPtr = Marshal.GetFunctionPointerForDelegate(_platformCreateWindow);
                _platformDestroyWindowPtr = Marshal.GetFunctionPointerForDelegate(_platformDestroyWindow);
                _platformShowWindowPtr = Marshal.GetFunctionPointerForDelegate(_platformShowWindow);
                _platformSetWindowPosPtr = Marshal.GetFunctionPointerForDelegate(_platformSetWindowPos);
                _platformGetWindowPosPtr = Marshal.GetFunctionPointerForDelegate(_platformGetWindowPos);
                _platformSetWindowSizePtr = Marshal.GetFunctionPointerForDelegate(_platformSetWindowSize);
                _platformGetWindowSizePtr = Marshal.GetFunctionPointerForDelegate(_platformGetWindowSize);
                _platformSetWindowFocusPtr = Marshal.GetFunctionPointerForDelegate(_platformSetWindowFocus);
                _platformGetWindowFocusPtr = Marshal.GetFunctionPointerForDelegate(_platformGetWindowFocus);
                _platformGetWindowMinimizedPtr = Marshal.GetFunctionPointerForDelegate(_platformGetWindowMinimized);
                _platformSetWindowTitlePtr = Marshal.GetFunctionPointerForDelegate(_platformSetWindowTitle);
                _platformSetWindowAlphaPtr = Marshal.GetFunctionPointerForDelegate(_platformSetWindowAlpha);
                _platformUpdateWindowPtr = Marshal.GetFunctionPointerForDelegate(_platformUpdateWindow);
                _platformRenderWindowPtr = Marshal.GetFunctionPointerForDelegate(_platformRenderWindow);
                _platformSwapBuffersPtr = Marshal.GetFunctionPointerForDelegate(_platformSwapBuffers);
                _platformGetWindowDpiScalePtr = Marshal.GetFunctionPointerForDelegate(_platformGetWindowDpiScale);
                _platformOnChangedViewportPtr = Marshal.GetFunctionPointerForDelegate(_platformOnChangedViewport);
                _rendererCreateWindowPtr = Marshal.GetFunctionPointerForDelegate(_rendererCreateWindow);
                _rendererDestroyWindowPtr = Marshal.GetFunctionPointerForDelegate(_rendererDestroyWindow);
                _rendererSetWindowSizePtr = Marshal.GetFunctionPointerForDelegate(_rendererSetWindowSize);
                _rendererRenderWindowPtr = Marshal.GetFunctionPointerForDelegate(_rendererRenderWindow);
                _rendererSwapBuffersPtr = Marshal.GetFunctionPointerForDelegate(_rendererSwapBuffers);
            }

            public static OpenGLImGuiMultiViewportController? TryCreate(OpenGLRenderer renderer, ImGuiController controller)
            {
                if (RenderImDrawData is null)
                {
                    Debug.RenderingWarning("ImGui multi-viewports disabled: Silk.NET ImGuiController RenderImDrawData hook was unavailable.");
                    return null;
                }

                if (renderer.XRWindow.Window.GLContext is null)
                {
                    Debug.RenderingWarning("ImGui multi-viewports disabled: the main OpenGL window has no GL context.");
                    return null;
                }

                return new OpenGLImGuiMultiViewportController(renderer, controller);
            }

            public void Install()
            {
                if (_installed || _disposed)
                    return;

                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                var platformIO = ImGui.GetPlatformIO();

                platformIO.NativePtr->Platform_CreateWindow = _platformCreateWindowPtr;
                platformIO.NativePtr->Platform_DestroyWindow = _platformDestroyWindowPtr;
                platformIO.NativePtr->Platform_ShowWindow = _platformShowWindowPtr;
                platformIO.NativePtr->Platform_SetWindowPos = _platformSetWindowPosPtr;
                ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowPos(platformIO.NativePtr, _platformGetWindowPosPtr);
                platformIO.NativePtr->Platform_SetWindowSize = _platformSetWindowSizePtr;
                ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowSize(platformIO.NativePtr, _platformGetWindowSizePtr);
                platformIO.NativePtr->Platform_SetWindowFocus = _platformSetWindowFocusPtr;
                platformIO.NativePtr->Platform_GetWindowFocus = _platformGetWindowFocusPtr;
                platformIO.NativePtr->Platform_GetWindowMinimized = _platformGetWindowMinimizedPtr;
                platformIO.NativePtr->Platform_SetWindowTitle = _platformSetWindowTitlePtr;
                platformIO.NativePtr->Platform_SetWindowAlpha = _platformSetWindowAlphaPtr;
                platformIO.NativePtr->Platform_UpdateWindow = _platformUpdateWindowPtr;
                platformIO.NativePtr->Platform_RenderWindow = _platformRenderWindowPtr;
                platformIO.NativePtr->Platform_SwapBuffers = _platformSwapBuffersPtr;
                platformIO.NativePtr->Platform_GetWindowDpiScale = _platformGetWindowDpiScalePtr;
                platformIO.NativePtr->Platform_OnChangedViewport = _platformOnChangedViewportPtr;
                platformIO.NativePtr->Renderer_CreateWindow = _rendererCreateWindowPtr;
                platformIO.NativePtr->Renderer_DestroyWindow = _rendererDestroyWindowPtr;
                platformIO.NativePtr->Renderer_SetWindowSize = _rendererSetWindowSizePtr;
                platformIO.NativePtr->Renderer_RenderWindow = _rendererRenderWindowPtr;
                platformIO.NativePtr->Renderer_SwapBuffers = _rendererSwapBuffersPtr;

                EnsureMainViewportPlatformData();
                UpdatePlatformMonitors();

                io.BackendFlags |=
                    ImGuiBackendFlags.PlatformHasViewports |
                    ImGuiBackendFlags.RendererHasViewports |
                    ImGuiBackendFlags.HasMouseHoveredViewport;
                io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
                _installed = true;

                Debug.Rendering("OpenGL ImGui multi-viewports enabled.");
            }

            public void QueueMainViewportInput()
            {
                if (!_installed || _disposed)
                    return;

                try
                {
                    _controller.MakeCurrent();
                    EnsureMainViewportPlatformData();

                    if (!TryGetMousePosition(out Vector2 position, out uint viewportId))
                        return;

                    var io = ImGui.GetIO();
                    io.AddMousePosEvent(position.X, position.Y);
                    io.AddMouseViewportEvent(viewportId);
                    io.MouseHoveredViewport = viewportId;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(QueueMainViewportInput), ex);
                }
            }

            public void UpdateMainViewportInput()
            {
                if (!_installed || _disposed)
                    return;

                try
                {
                    _controller.MakeCurrent();
                    EnsureMainViewportPlatformData();

                    if (!TryGetMousePosition(out Vector2 position, out uint viewportId))
                        return;

                    var io = ImGui.GetIO();
                    io.MousePos = position;
                    io.AddMouseViewportEvent(viewportId);
                    io.MouseHoveredViewport = viewportId;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(UpdateMainViewportInput), ex);
                }
            }

            public void RenderPlatformWindows()
            {
                if (!_installed || _disposed)
                    return;

                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
                    return;

                nint previousContext = ImGui.GetCurrentContext();
                try
                {
                    UpdatePlatformMonitors();
                    ImGui.UpdatePlatformWindows();
                    ImGui.RenderPlatformWindowsDefault();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(RenderPlatformWindows), ex);
                }
                finally
                {
                    try
                    {
                        _mainWindow.MakeCurrent();
                    }
                    catch (Exception ex)
                    {
                        LogCallbackException("RestoreMainOpenGLContext", ex);
                    }

                    if (previousContext == nint.Zero)
                    {
                        ImGui.SetCurrentContext(nint.Zero);
                    }
                    else if (ImGuiContextTracker.IsAlive(previousContext))
                    {
                        ImGui.SetCurrentContext(previousContext);
                    }
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;

                try
                {
                    _controller.MakeCurrent();

                    if (_installed && ImGuiContextTracker.IsAlive(_controller.Context))
                        ImGui.DestroyPlatformWindows();

                    ClearPlatformMonitors();
                    ClearPlatformCallbacks();

                    var io = ImGui.GetIO();
                    io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
                    io.BackendFlags &= ~(ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports | ImGuiBackendFlags.HasMouseHoveredViewport);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(Dispose), ex);
                }

                foreach (PlatformWindow window in _platformWindows.Values)
                    window.Dispose();
                _platformWindows.Clear();
            }

            private void ClearPlatformCallbacks()
            {
                var platformIO = ImGui.GetPlatformIO();
                platformIO.NativePtr->Platform_CreateWindow = nint.Zero;
                platformIO.NativePtr->Platform_DestroyWindow = nint.Zero;
                platformIO.NativePtr->Platform_ShowWindow = nint.Zero;
                platformIO.NativePtr->Platform_SetWindowPos = nint.Zero;
                platformIO.NativePtr->Platform_GetWindowPos = nint.Zero;
                platformIO.NativePtr->Platform_SetWindowSize = nint.Zero;
                platformIO.NativePtr->Platform_GetWindowSize = nint.Zero;
                platformIO.NativePtr->Platform_SetWindowFocus = nint.Zero;
                platformIO.NativePtr->Platform_GetWindowFocus = nint.Zero;
                platformIO.NativePtr->Platform_GetWindowMinimized = nint.Zero;
                platformIO.NativePtr->Platform_SetWindowTitle = nint.Zero;
                platformIO.NativePtr->Platform_SetWindowAlpha = nint.Zero;
                platformIO.NativePtr->Platform_UpdateWindow = nint.Zero;
                platformIO.NativePtr->Platform_RenderWindow = nint.Zero;
                platformIO.NativePtr->Platform_SwapBuffers = nint.Zero;
                platformIO.NativePtr->Platform_GetWindowDpiScale = nint.Zero;
                platformIO.NativePtr->Platform_OnChangedViewport = nint.Zero;
                platformIO.NativePtr->Renderer_CreateWindow = nint.Zero;
                platformIO.NativePtr->Renderer_DestroyWindow = nint.Zero;
                platformIO.NativePtr->Renderer_SetWindowSize = nint.Zero;
                platformIO.NativePtr->Renderer_RenderWindow = nint.Zero;
                platformIO.NativePtr->Renderer_SwapBuffers = nint.Zero;
                _installed = false;
            }

            private void EnsureMainViewportPlatformData()
            {
                ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
                mainViewport.PlatformHandle = _mainWindow.Handle;
                mainViewport.PlatformHandleRaw = _mainWindow.Handle;
                Vector2D<int> clientPosition = GetClientScreenPosition(_mainWindow);
                mainViewport.Pos = new Vector2(clientPosition.X, clientPosition.Y);
            }

            private bool TryGetMousePosition(out Vector2 position, out uint viewportId)
            {
                position = default;
                viewportId = 0;

                if (OperatingSystem.IsWindows() && GetCursorPos(out NativePoint cursorPosition))
                {
                    position = new Vector2(cursorPosition.X, cursorPosition.Y);
                    viewportId = ResolveHoveredViewportId(cursorPosition);
                    return true;
                }

                IInputContext? input = _renderer.XRWindow.Input;
                if (input is null || input.Mice.Count == 0)
                    return false;

                Vector2 localPosition = input.Mice[0].Position;
                Vector2D<int> clientPosition = GetClientScreenPosition(_mainWindow);
                position = new Vector2(clientPosition.X + localPosition.X, clientPosition.Y + localPosition.Y);
                viewportId = ImGui.GetMainViewport().ID;
                return true;
            }

            private uint ResolveHoveredViewportId(NativePoint screenPosition)
            {
                foreach (PlatformWindow window in _platformWindows.Values)
                {
                    if (window.IsDisposed)
                        continue;

                    if (TryGetWindowScreenRect(window.Window, out NativeRect rect) && rect.Contains(screenPosition))
                        return window.ViewportId;
                }

                if (TryGetWindowScreenRect(_mainWindow, out NativeRect mainRect) && mainRect.Contains(screenPosition))
                    return ImGui.GetMainViewport().ID;

                return 0;
            }

            private PlatformWindow? GetPlatformWindow(ImGuiViewportPtr viewport)
            {
                nint userData = viewport.PlatformUserData;
                if (userData == nint.Zero)
                    return null;

                try
                {
                    return GCHandle.FromIntPtr(userData).Target as PlatformWindow;
                }
                catch
                {
                    return null;
                }
            }

            private IWindow GetWindow(ImGuiViewportPtr viewport)
                => GetPlatformWindow(viewport)?.Window ?? _mainWindow;

            private void PlatformCreateWindow(ImGuiViewport* nativeViewport)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    if (_platformWindows.ContainsKey(viewport.ID))
                        return;

                    PlatformWindow window = new(this, viewport);
                    viewport.PlatformUserData = window.Handle;
                    viewport.PlatformHandle = window.Window.Handle;
                    viewport.PlatformHandleRaw = window.Window.Handle;
                    _platformWindows[viewport.ID] = window;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformCreateWindow), ex);
                }
            }

            private void PlatformDestroyWindow(ImGuiViewport* nativeViewport)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    PlatformWindow? window = GetPlatformWindow(viewport);
                    if (window is not null)
                    {
                        _platformWindows.Remove(window.ViewportId);
                        window.Dispose();
                    }

                    viewport.PlatformUserData = nint.Zero;
                    viewport.PlatformHandle = nint.Zero;
                    viewport.PlatformHandleRaw = nint.Zero;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformDestroyWindow), ex);
                }
            }

            private void PlatformShowWindow(ImGuiViewport* nativeViewport)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is { } window)
                        window.Window.IsVisible = true;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformShowWindow), ex);
                }
            }

            private void PlatformSetWindowPos(ImGuiViewport* nativeViewport, Vector2 position)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is { } window)
                        SetClientScreenPosition(window.Window, ToWindowPosition(position));
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSetWindowPos), ex);
                }
            }

            private void PlatformGetWindowPos(ImGuiViewport* nativeViewport, Vector2* outPosition)
            {
                try
                {
                    Vector2D<int> position = GetClientScreenPosition(GetWindow(new ImGuiViewportPtr(nativeViewport)));
                    *outPosition = new Vector2(position.X, position.Y);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowPos), ex);
                    *outPosition = Vector2.Zero;
                }
            }

            private void PlatformSetWindowSize(ImGuiViewport* nativeViewport, Vector2 size)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is { } window)
                        window.Window.Size = ToWindowSize(size);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSetWindowSize), ex);
                }
            }

            private void PlatformGetWindowSize(ImGuiViewport* nativeViewport, Vector2* outSize)
            {
                try
                {
                    Vector2D<int> size = GetWindow(new ImGuiViewportPtr(nativeViewport)).Size;
                    *outSize = new Vector2(size.X, size.Y);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowSize), ex);
                    *outSize = Vector2.One;
                }
            }

            private void PlatformSetWindowFocus(ImGuiViewport* nativeViewport)
            {
                try
                {
                    GetWindow(new ImGuiViewportPtr(nativeViewport)).Focus();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSetWindowFocus), ex);
                }
            }

            private byte PlatformGetWindowFocus(ImGuiViewport* nativeViewport)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    PlatformWindow? window = GetPlatformWindow(viewport);
                    if (window is not null)
                        return window.Focused ? (byte)1 : (byte)0;

                    return _renderer.XRWindow.IsFocused ? (byte)1 : (byte)0;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowFocus), ex);
                    return 0;
                }
            }

            private void UpdatePlatformMonitors()
            {
                _monitorScratch.Clear();

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        EnumDisplayMonitors(nint.Zero, nint.Zero, _monitorEnumProc, nint.Zero);
                    }
                    catch (Exception ex)
                    {
                        LogCallbackException(nameof(UpdatePlatformMonitors), ex);
                    }
                }

                if (_monitorScratch.Count == 0)
                    AddFallbackMonitor();

                WritePlatformMonitorBuffer();
            }

            private bool EnumMonitor(nint monitor, nint hdc, ref NativeRect rect, nint data)
            {
                NativeMonitorInfo monitorInfo = new()
                {
                    Size = Marshal.SizeOf<NativeMonitorInfo>()
                };

                if (!GetMonitorInfo(monitor, ref monitorInfo))
                    return true;

                _monitorScratch.Add(new ImGuiPlatformMonitor
                {
                    MainPos = new Vector2(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top),
                    MainSize = new Vector2(monitorInfo.Monitor.Width, monitorInfo.Monitor.Height),
                    WorkPos = new Vector2(monitorInfo.Work.Left, monitorInfo.Work.Top),
                    WorkSize = new Vector2(monitorInfo.Work.Width, monitorInfo.Work.Height),
                    DpiScale = GetMonitorDpiScale(monitor),
                    PlatformHandle = (void*)monitor
                });

                return true;
            }

            private void AddFallbackMonitor()
            {
                IMonitor? monitor = _mainWindow.Monitor;
                if (monitor is not null)
                {
                    Rectangle<int> bounds = monitor.Bounds;
                    _monitorScratch.Add(new ImGuiPlatformMonitor
                    {
                        MainPos = new Vector2(bounds.Origin.X, bounds.Origin.Y),
                        MainSize = new Vector2(bounds.Size.X, bounds.Size.Y),
                        WorkPos = new Vector2(bounds.Origin.X, bounds.Origin.Y),
                        WorkSize = new Vector2(bounds.Size.X, bounds.Size.Y),
                        DpiScale = 1.0f,
                        PlatformHandle = null
                    });
                    return;
                }

                Vector2D<int> position = GetClientScreenPosition(_mainWindow);
                Vector2D<int> size = _mainWindow.Size;
                _monitorScratch.Add(new ImGuiPlatformMonitor
                {
                    MainPos = new Vector2(position.X, position.Y),
                    MainSize = new Vector2(Math.Max(1, size.X), Math.Max(1, size.Y)),
                    WorkPos = new Vector2(position.X, position.Y),
                    WorkSize = new Vector2(Math.Max(1, size.X), Math.Max(1, size.Y)),
                    DpiScale = 1.0f,
                    PlatformHandle = null
                });
            }

            private void WritePlatformMonitorBuffer()
            {
                int count = _monitorScratch.Count;
                EnsureMonitorCapacity(count);

                var platformIO = ImGui.GetPlatformIO();
                var monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
                monitors->Size = count;
                monitors->Capacity = _monitorCapacity;
                monitors->Data = _monitorData;

                if (count == 0)
                    return;

                var destination = (ImGuiPlatformMonitor*)_monitorData;
                for (int i = 0; i < count; i++)
                    destination[i] = _monitorScratch[i];
            }

            private void EnsureMonitorCapacity(int count)
            {
                if (count <= _monitorCapacity)
                    return;

                if (_monitorData != nint.Zero)
                    Marshal.FreeHGlobal(_monitorData);

                int stride = sizeof(ImGuiPlatformMonitor);
                _monitorData = Marshal.AllocHGlobal(stride * count);
                _monitorCapacity = count;
            }

            private void ClearPlatformMonitors()
            {
                var platformIO = ImGui.GetPlatformIO();
                var monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
                monitors->Size = 0;
                monitors->Capacity = 0;
                monitors->Data = nint.Zero;

                if (_monitorData == nint.Zero)
                    return;

                Marshal.FreeHGlobal(_monitorData);
                _monitorData = nint.Zero;
                _monitorCapacity = 0;
            }

            private byte PlatformGetWindowMinimized(ImGuiViewport* nativeViewport)
            {
                try
                {
                    return GetWindow(new ImGuiViewportPtr(nativeViewport)).WindowState == WindowState.Minimized
                        ? (byte)1
                        : (byte)0;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowMinimized), ex);
                    return 0;
                }
            }

            private void PlatformSetWindowTitle(ImGuiViewport* nativeViewport, byte* title)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is not { } window)
                        return;

                    string? value = title is null ? null : Marshal.PtrToStringUTF8((nint)title);
                    if (!string.IsNullOrWhiteSpace(value))
                        window.Window.Title = value;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSetWindowTitle), ex);
                }
            }

            private void PlatformSetWindowAlpha(ImGuiViewport* nativeViewport, float alpha)
            {
                // Silk.NET's cross-platform window abstraction does not expose opacity.
                // Keeping this as a no-op matches Dear ImGui backend guidance: optional
                // callbacks may be left effectively unsupported when a platform can't apply them.
            }

            private void PlatformUpdateWindow(ImGuiViewport* nativeViewport)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    if (GetPlatformWindow(viewport) is not { } window)
                        return;

                    window.Window.DoEvents();
                    if (window.Window.IsClosing)
                        viewport.PlatformRequestClose = true;
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformUpdateWindow), ex);
                }
            }

            private void PlatformRenderWindow(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    if (GetPlatformWindow(viewport) is not { } window)
                        return;

                    window.Window.MakeCurrent();
                    Vector2D<int> framebufferSize = window.Window.FramebufferSize;
                    _renderer.Api.Viewport(0, 0, (uint)Math.Max(1, framebufferSize.X), (uint)Math.Max(1, framebufferSize.Y));

                    if ((viewport.Flags & ImGuiViewportFlags.NoRendererClear) == 0)
                    {
                        _renderer.Api.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
                        _renderer.Api.Clear(ClearBufferMask.ColorBufferBit);
                    }
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformRenderWindow), ex);
                }
            }

            private void PlatformSwapBuffers(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    if (GetPlatformWindow(new ImGuiViewportPtr(nativeViewport)) is { } window)
                        window.Window.GLContext?.SwapBuffers();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformSwapBuffers), ex);
                }
            }

            private float PlatformGetWindowDpiScale(ImGuiViewport* nativeViewport)
            {
                try
                {
                    IWindow window = GetWindow(new ImGuiViewportPtr(nativeViewport));
                    Vector2D<int> size = window.Size;
                    Vector2D<int> framebufferSize = window.FramebufferSize;
                    if (size.X <= 0 || size.Y <= 0)
                        return 1.0f;

                    float x = framebufferSize.X / (float)size.X;
                    float y = framebufferSize.Y / (float)size.Y;
                    return MathF.Max(MathF.Max(x, y), 1.0f);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowDpiScale), ex);
                    return 1.0f;
                }
            }

            private void PlatformOnChangedViewport(ImGuiViewport* nativeViewport)
            {
            }

            private void RendererCreateWindow(ImGuiViewport* nativeViewport)
            {
            }

            private void RendererDestroyWindow(ImGuiViewport* nativeViewport)
            {
            }

            private void RendererSetWindowSize(ImGuiViewport* nativeViewport, Vector2 size)
            {
            }

            private void RendererRenderWindow(ImGuiViewport* nativeViewport, void* renderArg)
            {
                try
                {
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    ImDrawDataPtr drawData = viewport.DrawData;
                    if (drawData.NativePtr is null)
                        return;

                    RenderImDrawData!(_controller, drawData);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(RendererRenderWindow), ex);
                }
            }

            private void RendererSwapBuffers(ImGuiViewport* nativeViewport, void* renderArg)
            {
            }

            private void RequestClose(uint viewportId)
            {
                ImGuiViewport* viewport = ImGuiNative.igFindViewportByID(viewportId);
                if (viewport is null)
                    return;

                new ImGuiViewportPtr(viewport).PlatformRequestClose = true;
            }

            private void PushMousePosition(uint viewportId, IWindow window, Vector2 localPosition)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                Vector2D<int> clientPosition = GetClientScreenPosition(window);
                io.AddMousePosEvent(clientPosition.X + localPosition.X, clientPosition.Y + localPosition.Y);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushMouseButton(uint viewportId, MouseButton button, bool down)
            {
                if (!TryTranslateMouseButton(button, out int imGuiButton))
                    return;

                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                io.AddMouseButtonEvent(imGuiButton, down);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushMouseWheel(uint viewportId, ScrollWheel wheel)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                io.AddMouseWheelEvent(wheel.X, wheel.Y);
                io.AddMouseViewportEvent(viewportId);
            }

            private void PushKey(IKeyboard keyboard, Key key, int scancode, bool down)
            {
                _controller.MakeCurrent();
                var io = ImGui.GetIO();
                ImGuiKey imGuiKey = TranslateKey(key);
                io.AddKeyEvent(imGuiKey, down);
                io.SetKeyEventNativeData(imGuiKey, (int)key, scancode);
                io.KeyCtrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
                io.KeyAlt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
                io.KeyShift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
                io.KeySuper = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
            }

            private void PushChar(char value)
            {
                _controller.MakeCurrent();
                ImGui.GetIO().AddInputCharacter(value);
            }

            private static bool TryTranslateMouseButton(MouseButton button, out int imGuiButton)
            {
                switch (button)
                {
                    case MouseButton.Left:
                        imGuiButton = 0;
                        return true;
                    case MouseButton.Right:
                        imGuiButton = 1;
                        return true;
                    case MouseButton.Middle:
                        imGuiButton = 2;
                        return true;
                    default:
                        imGuiButton = -1;
                        return false;
                }
            }

            private static ImGuiKey TranslateKey(Key key)
            {
                if (TranslateInputKey is not null)
                    return TranslateInputKey(key);

                return key switch
                {
                    Key.Tab => ImGuiKey.Tab,
                    Key.Left => ImGuiKey.LeftArrow,
                    Key.Right => ImGuiKey.RightArrow,
                    Key.Up => ImGuiKey.UpArrow,
                    Key.Down => ImGuiKey.DownArrow,
                    Key.PageUp => ImGuiKey.PageUp,
                    Key.PageDown => ImGuiKey.PageDown,
                    Key.Home => ImGuiKey.Home,
                    Key.End => ImGuiKey.End,
                    Key.Insert => ImGuiKey.Insert,
                    Key.Delete => ImGuiKey.Delete,
                    Key.Backspace => ImGuiKey.Backspace,
                    Key.Space => ImGuiKey.Space,
                    Key.Enter => ImGuiKey.Enter,
                    Key.Escape => ImGuiKey.Escape,
                    Key.A => ImGuiKey.A,
                    Key.C => ImGuiKey.C,
                    Key.V => ImGuiKey.V,
                    Key.X => ImGuiKey.X,
                    Key.Y => ImGuiKey.Y,
                    Key.Z => ImGuiKey.Z,
                    _ => ImGuiKey.None,
                };
            }

            private static Vector2D<int> ToWindowSize(Vector2 size)
                => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

            private static Vector2D<int> ToWindowPosition(Vector2 position)
                => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

            private static Vector2D<int> GetClientScreenPosition(IWindow window)
            {
                if (OperatingSystem.IsWindows() && window.Handle != nint.Zero)
                {
                    var point = new NativePoint();
                    if (ClientToScreen(window.Handle, ref point))
                        return new Vector2D<int>(point.X, point.Y);
                }

                return window.Position;
            }

            private static bool TryGetWindowScreenRect(IWindow window, out NativeRect rect)
            {
                if (OperatingSystem.IsWindows() && window.Handle != nint.Zero && GetWindowRect(window.Handle, out rect))
                    return true;

                Vector2D<int> position = GetClientScreenPosition(window);
                Vector2D<int> size = window.Size;
                rect = new NativeRect
                {
                    Left = position.X,
                    Top = position.Y,
                    Right = position.X + Math.Max(1, size.X),
                    Bottom = position.Y + Math.Max(1, size.Y)
                };
                return true;
            }

            private static void SetClientScreenPosition(IWindow window, Vector2D<int> targetClientPosition)
            {
                Vector2D<int> clientPosition = GetClientScreenPosition(window);
                Vector2D<int> windowPosition = window.Position;
                Vector2D<int> clientOffset = clientPosition - windowPosition;
                window.Position = targetClientPosition - clientOffset;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativePoint
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;

                public int Width => Right - Left;
                public int Height => Bottom - Top;

                public readonly bool Contains(NativePoint point)
                    => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            private struct NativeMonitorInfo
            {
                public int Size;
                public NativeRect Monitor;
                public NativeRect Work;
                public uint Flags;
            }

            private enum MonitorDpiType
            {
                Effective = 0
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MutableImVector
            {
                public int Size;
                public int Capacity;
                public nint Data;
            }

            private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref NativeRect rect, nint data);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetCursorPos(out NativePoint point);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc callback, nint data);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo monitorInfo);

            [DllImport("shcore.dll")]
            private static extern int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

            private static float GetMonitorDpiScale(nint monitor)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(6, 3))
                    return 1.0f;

                try
                {
                    return GetDpiForMonitor(monitor, MonitorDpiType.Effective, out uint dpiX, out uint dpiY) == 0
                        ? MathF.Max(MathF.Max(dpiX, dpiY) / 96.0f, 1.0f)
                        : 1.0f;
                }
                catch
                {
                    return 1.0f;
                }
            }

            private static RenderImDrawDataDelegate? CreateRenderImDrawDataDelegate()
            {
                try
                {
                    MethodInfo? method = typeof(ImGuiController).GetMethod("RenderImDrawData", BindingFlags.Instance | BindingFlags.NonPublic);
                    return method?.CreateDelegate<RenderImDrawDataDelegate>();
                }
                catch
                {
                    return null;
                }
            }

            private static TranslateInputKeyDelegate? CreateTranslateInputKeyDelegate()
            {
                try
                {
                    MethodInfo? method = typeof(ImGuiController).GetMethod("TranslateInputKeyToImGuiKey", BindingFlags.Static | BindingFlags.NonPublic);
                    return method?.CreateDelegate<TranslateInputKeyDelegate>();
                }
                catch
                {
                    return null;
                }
            }

            private static void LogCallbackException(string callback, Exception ex)
            {
                Debug.RenderingWarningEvery(
                    $"OpenGL.ImGui.MultiViewport.{callback}",
                    TimeSpan.FromSeconds(2),
                    "[ImGuiMultiViewport] {0} failed: {1}",
                    callback,
                    ex.Message);
            }

            private sealed class PlatformWindow : IDisposable
            {
                private readonly OpenGLImGuiMultiViewportController _owner;
                private readonly GCHandle _handle;
                private IInputContext? _input;
                private IMouse? _mouse;
                private readonly List<IKeyboard> _keyboards = [];
                private bool _disposed;

                public PlatformWindow(OpenGLImGuiMultiViewportController owner, ImGuiViewportPtr viewport)
                {
                    _owner = owner;
                    ViewportId = viewport.ID;
                    _handle = GCHandle.Alloc(this);

                    var options = WindowOptions.Default;
                    options.API = owner._mainWindow.API;
                    options.SharedContext = owner._mainWindow.GLContext;
                    options.Size = ToWindowSize(viewport.Size);
                    options.Position = ToWindowPosition(viewport.Pos);
                    options.Title = "XREngine";
                    options.WindowBorder = (viewport.Flags & ImGuiViewportFlags.NoDecoration) != 0
                        ? WindowBorder.Hidden
                        : WindowBorder.Resizable;
                    options.TopMost = (viewport.Flags & ImGuiViewportFlags.TopMost) != 0;
                    options.IsVisible = false;
                    options.ShouldSwapAutomatically = false;

                    Window = Silk.NET.Windowing.Window.Create(options);
                    Window.Load += OnLoad;
                    Window.FocusChanged += OnFocusChanged;
                    Window.Closing += OnClosing;
                    Window.Initialize();
                    SetClientScreenPosition(Window, ToWindowPosition(viewport.Pos));
                    Window.MakeCurrent();
                    Window.GLContext?.SwapInterval(0);
                }

                public IWindow Window { get; }
                public uint ViewportId { get; }
                public bool Focused { get; private set; }
                public bool IsDisposed => _disposed;
                public nint Handle => GCHandle.ToIntPtr(_handle);

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;

                    Window.Load -= OnLoad;
                    Window.FocusChanged -= OnFocusChanged;
                    Window.Closing -= OnClosing;
                    DetachInput();

                    try
                    {
                        (Window as IDisposable)?.Dispose();
                    }
                    catch
                    {
                    }

                    if (_handle.IsAllocated)
                        _handle.Free();
                }

                private void OnLoad()
                {
                    try
                    {
                        _input = Window.CreateInput();
                        AttachInput();
                    }
                    catch (Exception ex)
                    {
                        LogCallbackException("PlatformWindow.OnLoad", ex);
                    }
                }

                private void AttachInput()
                {
                    if (_input is null)
                        return;

                    if (_input.Mice.Count > 0)
                    {
                        _mouse = _input.Mice[0];
                        _mouse.MouseMove += OnMouseMove;
                        _mouse.MouseDown += OnMouseDown;
                        _mouse.MouseUp += OnMouseUp;
                        _mouse.Scroll += OnMouseScroll;
                    }

                    foreach (IKeyboard keyboard in _input.Keyboards)
                    {
                        keyboard.KeyDown += OnKeyDown;
                        keyboard.KeyUp += OnKeyUp;
                        keyboard.KeyChar += OnKeyChar;
                        _keyboards.Add(keyboard);
                    }
                }

                private void DetachInput()
                {
                    if (_mouse is not null)
                    {
                        _mouse.MouseMove -= OnMouseMove;
                        _mouse.MouseDown -= OnMouseDown;
                        _mouse.MouseUp -= OnMouseUp;
                        _mouse.Scroll -= OnMouseScroll;
                        _mouse = null;
                    }

                    foreach (IKeyboard keyboard in _keyboards)
                    {
                        keyboard.KeyDown -= OnKeyDown;
                        keyboard.KeyUp -= OnKeyUp;
                        keyboard.KeyChar -= OnKeyChar;
                    }
                    _keyboards.Clear();

                    try
                    {
                        (_input as IDisposable)?.Dispose();
                    }
                    catch
                    {
                    }

                    _input = null;
                }

                private void OnFocusChanged(bool focused)
                    => Focused = focused;

                private void OnClosing()
                {
                    _owner.RequestClose(ViewportId);

                    try
                    {
                        Window.IsClosing = false;
                    }
                    catch
                    {
                    }
                }

                private void OnMouseMove(IMouse mouse, Vector2 position)
                    => _owner.PushMousePosition(ViewportId, Window, position);

                private void OnMouseDown(IMouse mouse, MouseButton button)
                    => _owner.PushMouseButton(ViewportId, button, true);

                private void OnMouseUp(IMouse mouse, MouseButton button)
                    => _owner.PushMouseButton(ViewportId, button, false);

                private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
                    => _owner.PushMouseWheel(ViewportId, wheel);

                private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
                    => _owner.PushKey(keyboard, key, scancode, true);

                private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
                    => _owner.PushKey(keyboard, key, scancode, false);

                private void OnKeyChar(IKeyboard keyboard, char value)
                    => _owner.PushChar(value);
            }
        }
    }
}
