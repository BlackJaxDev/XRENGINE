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
        private sealed unsafe partial class OpenGLImGuiMultiViewportController : IDisposable
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
            private static readonly List<IWindow> AbandonedShutdownWindows = [];
            private static readonly List<IInputContext> AbandonedShutdownInputContexts = [];
            private static readonly bool DisposeNativeViewportWindows =
                string.Equals(
                    Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.ImGuiViewportDisposeNative),
                    "1",
                    StringComparison.Ordinal);

            private readonly OpenGLRenderer _renderer;
            private readonly ImGuiController _controller;
            private readonly IWindow _mainWindow;
            private readonly Dictionary<uint, PlatformWindow> _platformWindows = [];
            private readonly List<PendingPlatformWindowDisposal> _pendingPlatformWindowDisposals = [];
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
            private const int PlatformWindowDisposalQuietFrames = 2;

            private bool _installed;
            private bool _disposed;
            private bool _lastLeftMouseDown;
            private bool _lastRightMouseDown;
            private bool _lastMiddleMouseDown;
            private IMouse? _mainMouse;
            private readonly Queue<Vector2> _pendingMainMouseWheelDeltas = [];
            private readonly List<Vector2> _mainMouseWheelDispatchBuffer = [];
            private readonly object _mainMouseWheelLock = new();
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

            /// <summary>
            /// Create and initialize a controller only when all required ImGui hooks are available.
            /// </summary>
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

            /// <summary>
            /// Wires ImGui platform/renderer callbacks and enables multi-viewport behavior.
            /// </summary>
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
                AttachMainInput();

                io.BackendFlags |=
                    ImGuiBackendFlags.PlatformHasViewports |
                    ImGuiBackendFlags.RendererHasViewports |
                    ImGuiBackendFlags.HasMouseHoveredViewport;
                io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
                _installed = true;

                Debug.Rendering("OpenGL ImGui multi-viewports enabled.");
            }

            /// <summary>
            /// Pushes one-time mouse state transition data for the main platform window.
            /// </summary>
            public void QueueMainViewportInput()
            {
                if (!_installed || _disposed)
                    return;

                try
                {
                    _controller.MakeCurrent();
                    AttachMainInput();
                    EnsureMainViewportPlatformData();

                    if (!TryGetMousePosition(out Vector2 position, out uint viewportId))
                        return;

                    var io = ImGui.GetIO();
                    io.AddMousePosEvent(position.X, position.Y);
                    io.AddMouseViewportEvent(viewportId);
                    io.MouseHoveredViewport = viewportId;
                    QueueMainMouseButtonEvents(io);
                    QueueMainMouseWheelEvents(io, viewportId);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(QueueMainViewportInput), ex);
                }
            }

            /// <summary>
            /// Continuously updates main-window hover and cursor state in ImGui IO.
            /// </summary>
            public void UpdateMainViewportInput()
            {
                if (!_installed || _disposed)
                    return;

                try
                {
                    _controller.MakeCurrent();
                    AttachMainInput();
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

            public void ClearQueuedMainMouseWheelEvents()
            {
                lock (_mainMouseWheelLock)
                    _pendingMainMouseWheelDeltas.Clear();
            }

            /// <summary>
            /// Updates and renders ImGui platform windows, then restores the primary OpenGL context.
            /// </summary>
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
                    DetachMainInput();

                    var io = ImGui.GetIO();
                    io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
                    io.BackendFlags &= ~(ImGuiBackendFlags.PlatformHasViewports | ImGuiBackendFlags.RendererHasViewports | ImGuiBackendFlags.HasMouseHoveredViewport);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(Dispose), ex);
                }

                foreach (PlatformWindow window in _platformWindows.Values)
                    window.AbandonNativeWindowForShutdown();
                _platformWindows.Clear();
                AbandonPendingPlatformWindowsForShutdown();
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

            private void QueuePlatformWindowDispose(PlatformWindow window)
            {
                if (!window.BeginDispose())
                    return;

                // GLFW window destruction can re-enter native close/event handling.
                // Docking back into the main viewport can also retire a platform
                // window while the drag mouse button is still down, so wait for a
                // short quiet period before destroying the native window.
                _pendingPlatformWindowDisposals.Add(new PendingPlatformWindowDisposal(window));
            }

            private void DisposePendingPlatformWindows(bool force = false)
            {
                if (_pendingPlatformWindowDisposals.Count == 0)
                    return;

                bool mouseButtonsReleased = force || AreMouseButtonsReleased();
                bool preparedMainContext = false;
                int writeIndex = 0;

                for (int i = 0; i < _pendingPlatformWindowDisposals.Count; i++)
                {
                    PendingPlatformWindowDisposal pending = _pendingPlatformWindowDisposals[i];

                    if (!force && !mouseButtonsReleased)
                    {
                        pending.QuietFramesRemaining = PlatformWindowDisposalQuietFrames;
                        _pendingPlatformWindowDisposals[writeIndex++] = pending;
                        continue;
                    }

                    if (!force && pending.QuietFramesRemaining > 0)
                    {
                        pending.QuietFramesRemaining--;
                        _pendingPlatformWindowDisposals[writeIndex++] = pending;
                        continue;
                    }

                    if (!preparedMainContext)
                    {
                        preparedMainContext = true;
                        try
                        {
                            _mainWindow.MakeCurrent();
                        }
                        catch (Exception ex)
                        {
                            LogCallbackException("PrepareMainOpenGLContextForViewportDispose", ex);
                        }
                    }

                    pending.Window.ReleaseAfterRuntimeClose();
                }

                if (writeIndex < _pendingPlatformWindowDisposals.Count)
                    _pendingPlatformWindowDisposals.RemoveRange(writeIndex, _pendingPlatformWindowDisposals.Count - writeIndex);
            }

            private void AbandonPendingPlatformWindowsForShutdown()
            {
                if (_pendingPlatformWindowDisposals.Count == 0)
                    return;

                for (int i = 0; i < _pendingPlatformWindowDisposals.Count; i++)
                    _pendingPlatformWindowDisposals[i].Window.AbandonNativeWindowForShutdown();

                _pendingPlatformWindowDisposals.Clear();
            }

            private bool AreMouseButtonsReleased()
            {
                if (!TryReadMouseButtonState(out bool leftDown, out bool rightDown, out bool middleDown))
                    return false;

                return !leftDown && !rightDown && !middleDown;
            }

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
                        QueuePlatformWindowDispose(window);
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



            private struct PendingPlatformWindowDisposal
            {
                public PendingPlatformWindowDisposal(PlatformWindow window)
                {
                    Window = window;
                    QuietFramesRemaining = PlatformWindowDisposalQuietFrames;
                }

                public PlatformWindow Window;
                public int QuietFramesRemaining;
            }

            private sealed class PlatformWindow : IDisposable
            {
                private readonly OpenGLImGuiMultiViewportController _owner;
                private readonly GCHandle _handle;
                private IInputContext? _input;
                private IMouse? _mouse;
                private readonly List<IKeyboard> _keyboards = [];
                private bool _disposeStarted;
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
                public bool IsDisposed => _disposeStarted;
                public nint Handle => GCHandle.ToIntPtr(_handle);

                public bool BeginDispose()
                {
                    if (_disposeStarted)
                        return false;

                    _disposeStarted = true;

                    Window.Load -= OnLoad;
                    Window.FocusChanged -= OnFocusChanged;
                    Window.Closing -= OnClosing;
                    DetachInputHandlers();

                    try
                    {
                        Window.IsVisible = false;
                    }
                    catch
                    {
                    }

                    return true;
                }

                public void Dispose()
                {
                    BeginDispose();

                    if (_disposed)
                        return;

                    _disposed = true;

                    try
                    {
                        (Window as IDisposable)?.Dispose();
                        ReleaseInputContext();
                    }
                    catch
                    {
                        AbandonInputContextForShutdown();
                        AbandonedShutdownWindows.Add(Window);
                    }

                    if (_handle.IsAllocated)
                        _handle.Free();
                }

                public void ReleaseAfterRuntimeClose()
                {
                    if (DisposeNativeViewportWindows)
                    {
                        Dispose();
                        return;
                    }

                    // Native window disposal can block inside the GLFW/Silk close path
                    // when ImGui retires a platform viewport during the frame. Hide and
                    // detach it instead; the process owns these short-lived editor
                    // windows and can reclaim them on shutdown.
                    AbandonNativeWindowForShutdown();
                }

                public void AbandonNativeWindowForShutdown()
                {
                    BeginDispose();

                    if (_disposed)
                        return;

                    _disposed = true;

                    if (_handle.IsAllocated)
                        _handle.Free();

                    AbandonInputContextForShutdown();
                    AbandonedShutdownWindows.Add(Window);
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

                private void DetachInputHandlers()
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
                }

                private void ReleaseInputContext()
                {
                    // Silk.NET.Input.Glfw unregisters GLFW callbacks by calling glfwSet*Callback
                    // during Dispose. Destroying a short-lived ImGui viewport already tears down
                    // the native GLFW window, so avoid callback restoration on this crash-prone path.
                    _input = null;
                }

                private void AbandonInputContextForShutdown()
                {
                    if (_input is null)
                        return;

                    AbandonedShutdownInputContexts.Add(_input);
                    _input = null;
                }

                private void OnFocusChanged(bool focused)
                    => Focused = focused;

                private void OnClosing()
                {
                    if (_disposeStarted)
                        return;

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
