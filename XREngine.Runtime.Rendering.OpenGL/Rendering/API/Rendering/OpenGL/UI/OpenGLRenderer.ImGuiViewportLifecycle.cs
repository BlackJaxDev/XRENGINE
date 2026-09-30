using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;

namespace XREngine.Rendering.OpenGL
{
    public partial class OpenGLRenderer
    {
        private sealed unsafe partial class OpenGLImGuiMultiViewportController : IDisposable
        {

            private static readonly List<IRuntimeWindowBackend> AbandonedShutdownWindows = [];
            private static bool DisposeNativeViewportWindows
                => XREnvironment.IsEnabled(XREngineEnvironmentVariables.ImGuiViewportDisposeNative);

            private readonly OpenGLRenderer _renderer;
            private OpenGLImGuiController? _controller;
            private readonly nint _context;
            private readonly IRuntimeWindowBackend _mainWindow;
            private readonly Dictionary<uint, PlatformWindow> _platformWindows = [];
            private readonly List<PendingPlatformWindowDisposal> _pendingPlatformWindowDisposals = [];
            private readonly List<ImGuiPlatformMonitor> _monitorScratch = [];


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
            private nint _monitorData;
            private int _monitorCapacity;
            private IDisposable? _callbackRegistration;

            private OpenGLImGuiMultiViewportController(OpenGLRenderer renderer, nint context)
            {
                _renderer = renderer;
                _context = context;
                _mainWindow = renderer.XRWindow.DesktopWindowBackend
                    ?? throw new InvalidOperationException("OpenGL ImGui multi-viewports require a desktop window backend.");

                _platformCreateWindowPtr = RendererImGuiViewportCallbackBridge.PlatformCreateWindow;
                _platformDestroyWindowPtr = RendererImGuiViewportCallbackBridge.PlatformDestroyWindow;
                _platformShowWindowPtr = RendererImGuiViewportCallbackBridge.PlatformShowWindow;
                _platformSetWindowPosPtr = RendererImGuiViewportCallbackBridge.PlatformSetWindowPosition;
                _platformGetWindowPosPtr = RendererImGuiViewportCallbackBridge.PlatformGetWindowPosition;
                _platformSetWindowSizePtr = RendererImGuiViewportCallbackBridge.PlatformSetWindowSize;
                _platformGetWindowSizePtr = RendererImGuiViewportCallbackBridge.PlatformGetWindowSize;
                _platformSetWindowFocusPtr = RendererImGuiViewportCallbackBridge.PlatformSetWindowFocus;
                _platformGetWindowFocusPtr = RendererImGuiViewportCallbackBridge.PlatformGetWindowFocus;
                _platformGetWindowMinimizedPtr = RendererImGuiViewportCallbackBridge.PlatformGetWindowMinimized;
                _platformSetWindowTitlePtr = RendererImGuiViewportCallbackBridge.PlatformSetWindowTitle;
                _platformSetWindowAlphaPtr = RendererImGuiViewportCallbackBridge.PlatformSetWindowAlpha;
                _platformUpdateWindowPtr = RendererImGuiViewportCallbackBridge.PlatformUpdateWindow;
                _platformRenderWindowPtr = RendererImGuiViewportCallbackBridge.PlatformRenderWindow;
                _platformSwapBuffersPtr = RendererImGuiViewportCallbackBridge.PlatformSwapBuffers;
                _platformGetWindowDpiScalePtr = RendererImGuiViewportCallbackBridge.PlatformGetWindowDpiScale;
                _platformOnChangedViewportPtr = RendererImGuiViewportCallbackBridge.PlatformOnChangedViewport;
                _rendererCreateWindowPtr = RendererImGuiViewportCallbackBridge.RendererCreateWindow;
                _rendererDestroyWindowPtr = RendererImGuiViewportCallbackBridge.RendererDestroyWindow;
                _rendererSetWindowSizePtr = RendererImGuiViewportCallbackBridge.RendererSetWindowSize;
                _rendererRenderWindowPtr = RendererImGuiViewportCallbackBridge.RendererRenderWindow;
                _rendererSwapBuffersPtr = RendererImGuiViewportCallbackBridge.RendererSwapBuffers;
            }

            /// <summary>
            /// Create and initialize a controller only when all required ImGui hooks are available.
            /// </summary>
            public static OpenGLImGuiMultiViewportController? TryCreate(OpenGLRenderer renderer)
            {
                if (renderer.XRWindow.NativeWindowThreadId != renderer.XRWindow.RenderOwnerThreadId)
                {
                    Debug.RenderingWarning(
                        "ImGui multi-viewports require a collapsed desktop window/render owner; this split window topology does not support synchronous viewport creation.");
                    return null;
                }

                if (renderer.XRWindow.DesktopWindowBackend?.GlContext is null)
                {
                    Debug.RenderingWarning("ImGui multi-viewports disabled: the main OpenGL window has no GL context.");
                    return null;
                }

                nint context = ImGui.GetCurrentContext();
                if (context == nint.Zero)
                {
                    Debug.RenderingWarning("ImGui multi-viewports disabled: no current ImGui context was available during controller configuration.");
                    return null;
                }

                return new OpenGLImGuiMultiViewportController(renderer, context);
            }

            /// <summary>
            /// Attaches the editor controller after its ImGui context and GL objects are ready.
            /// </summary>
            public void AttachController(OpenGLImGuiController controller)
            {
                if (controller.Context != _context)
                    throw new InvalidOperationException("Cannot attach an ImGui controller for a different context.");

                _controller = controller;
            }

            private void MakeCurrent()
            {
                if (_controller is { } controller)
                    controller.MakeCurrent();
                else
                    ImGui.SetCurrentContext(_context);
            }

            /// <summary>
            /// Wires ImGui platform/renderer callbacks and enables multi-viewport behavior.
            /// </summary>
            public void Install()
            {
                if (_installed || _disposed)
                    return;

                MakeCurrent();
                _callbackRegistration = RendererImGuiViewportCallbackBridge.Register(
                    _context,
                    this);
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
                LogInitialViewportState();

                io.BackendFlags |=
                    ImGuiBackendFlags.PlatformHasViewports |
                    ImGuiBackendFlags.RendererHasViewports |
                    ImGuiBackendFlags.HasMouseHoveredViewport;
                io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
                PrepareImplicitWindowForNewFrame();
                _installed = true;

                Debug.Rendering("OpenGL ImGui multi-viewports enabled.");
            }

            /// <summary>
            /// Keeps Dear ImGui's hidden fallback window on the application-owned main viewport.
            /// </summary>
            /// <remarks>
            /// NewFrame begins an implicit Debug##Default window before application UI runs and
            /// parks it at an off-screen sentinel while hidden. With multiple physical monitors,
            /// allowing that internal window to own a platform viewport leaves it without a
            /// monitor or DPI. SetNextWindowViewport is consumed by that implicit Begin call and
            /// does not affect the first application window drawn later in the frame.
            /// </remarks>
            private static void PrepareImplicitWindowForNewFrame()
                => ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);

            private void LogInitialViewportState()
            {
                ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
                var platformIO = ImGui.GetPlatformIO();
                var monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
                Debug.Rendering(
                    $"[ImGuiMultiViewport] Initial main viewport: pos={mainViewport.Pos}, size={mainViewport.Size}, dpi={mainViewport.DpiScale}, platformCreated={mainViewport.PlatformWindowCreated}; monitors={monitors->Size}.");

                for (int i = 0; i < _monitorScratch.Count; i++)
                {
                    ImGuiPlatformMonitor monitor = _monitorScratch[i];
                    Debug.Rendering(
                        $"[ImGuiMultiViewport] Monitor {i}: mainPos={monitor.MainPos}, mainSize={monitor.MainSize}, workPos={monitor.WorkPos}, workSize={monitor.WorkSize}, dpi={monitor.DpiScale}.");
                }
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
                    MakeCurrent();
                    PrepareImplicitWindowForNewFrame();
                    EnsureMainViewportPlatformData();

                    var io = ImGui.GetIO();
                    foreach (PlatformWindow window in _platformWindows.Values)
                        window.DrainInput(io);

                    if (!_mainWindow.Events.IsFocused)
                        return;

                    if (!TryGetMousePosition(out Vector2 position, out uint viewportId))
                        return;

                    io.AddMousePosEvent(position.X, position.Y);
                    io.AddMouseViewportEvent(viewportId);
                    io.MouseHoveredViewport = viewportId;
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
                    MakeCurrent();
                    EnsureMainViewportPlatformData();

                    if (!_mainWindow.Events.IsFocused)
                        return;

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
                    MakeCurrent();

                    if (_installed && ImGuiContextTracker.IsAlive(_context))
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
                    window.AbandonNativeWindowForShutdown();
                _platformWindows.Clear();
                AbandonPendingPlatformWindowsForShutdown();
                Interlocked.Exchange(ref _callbackRegistration, null)?.Dispose();
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

            private IRuntimeWindowBackend GetWindow(ImGuiViewportPtr viewport)
            {
                if (viewport.ID == ImGui.GetMainViewport().ID)
                    return _mainWindow;
                return GetPlatformWindow(viewport)?.Window
                    ?? throw new InvalidOperationException($"ImGui viewport {viewport.ID} has no registered desktop window.");
            }

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
                            _mainWindow.GlContext?.MakeCurrent();
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
                    viewport.PlatformHandle = window.Window.PlatformWindowHandle;
                    viewport.PlatformHandleRaw = window.Window.OperatingSystemWindowHandle;
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
                    var viewport = new ImGuiViewportPtr(nativeViewport);
                    if (GetPlatformWindow(viewport) is not { } window)
                        return;

                    window.UpdateViewportFlags(viewport.Flags);
                    if (!ImGuiPlatformWindowBehavior.TryShowWithoutActivation(window.Window.OperatingSystemWindowHandle, (uint)viewport.Flags))
                        window.Window.RequestVisibility(true);
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
                    IVector2 position = GetClientScreenPosition(GetWindow(new ImGuiViewportPtr(nativeViewport)));
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
                        window.Window.RequestSize(ToWindowSize(size));
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
                    WindowSurfaceSnapshot surface = GetWindow(new ImGuiViewportPtr(nativeViewport)).Surface;
                    *outSize = new Vector2(surface.ClientWidth, surface.ClientHeight);
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
                    GetWindow(new ImGuiViewportPtr(nativeViewport)).RequestFocus();
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
                ReadOnlySpan<RuntimeDesktopMonitor> monitors = _mainWindow.Monitors.Span;
                for (int i = 0; i < monitors.Length; i++)
                {
                    RuntimeDesktopMonitor monitor = monitors[i];
                    _monitorScratch.Add(new ImGuiPlatformMonitor
                    {
                        MainPos = new Vector2(monitor.X, monitor.Y),
                        MainSize = new Vector2(monitor.Width, monitor.Height),
                        WorkPos = new Vector2(monitor.WorkX, monitor.WorkY),
                        WorkSize = new Vector2(monitor.WorkWidth, monitor.WorkHeight),
                        DpiScale = monitor.DpiScale,
                        PlatformHandle = (void*)monitor.PlatformHandle,
                    });
                }

                if (_monitorScratch.Count == 0)
                    AddFallbackMonitor();

                WritePlatformMonitorBuffer();
            }

            private void AddFallbackMonitor()
            {
                IVector2 position = GetClientScreenPosition(_mainWindow);
                WindowSurfaceSnapshot surface = _mainWindow.Surface;
                _monitorScratch.Add(new ImGuiPlatformMonitor
                {
                    MainPos = new Vector2(position.X, position.Y),
                    MainSize = new Vector2(Math.Max(1, surface.ClientWidth), Math.Max(1, surface.ClientHeight)),
                    WorkPos = new Vector2(position.X, position.Y),
                    WorkSize = new Vector2(Math.Max(1, surface.ClientWidth), Math.Max(1, surface.ClientHeight)),
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
                    return GetWindow(new ImGuiViewportPtr(nativeViewport)).Surface.IsMinimized
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
                        window.Window.RequestTitle(value);
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

                    window.UpdateViewportFlags(viewport.Flags);
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
                    IRuntimeWindowBackend window = GetWindow(new ImGuiViewportPtr(nativeViewport));
                    return GetWindowDpiScale(window);
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(PlatformGetWindowDpiScale), ex);
                    return 1.0f;
                }
            }

            private static float GetWindowDpiScale(IRuntimeWindowBackend window)
            {
                WindowSurfaceSnapshot surface = window.Surface;
                float x = surface.DpiScaleX;
                float y = surface.DpiScaleY;
                float scale = MathF.Max(x, y);
                return float.IsFinite(scale) && scale > 0.0f && scale < 99.0f
                    ? scale
                    : 1.0f;
            }

            private void PlatformOnChangedViewport(ImGuiViewport* nativeViewport)
            {
                var viewport = new ImGuiViewportPtr(nativeViewport);
                if (float.IsFinite(viewport.DpiScale) && viewport.DpiScale > 0.0f && viewport.DpiScale < 99.0f)
                    return;

                var platformIO = ImGui.GetPlatformIO();
                var monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
                Debug.RenderingWarning(
                    $"[ImGuiMultiViewport] Invalid viewport DPI selected: id=0x{viewport.ID:X8}, flags={viewport.Flags}, pos={viewport.Pos}, size={viewport.Size}, dpi={viewport.DpiScale}, monitors={monitors->Size}.");
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
                private bool _disposeStarted;
                private bool _disposed;

                public PlatformWindow(OpenGLImGuiMultiViewportController owner, ImGuiViewportPtr viewport)
                {
                    _owner = owner;
                    ViewportId = viewport.ID;
                    ViewportFlags = viewport.Flags;
                    _handle = GCHandle.Alloc(this);

                    var size = ToWindowSize(viewport.Size);
                    var position = ToWindowPosition(viewport.Pos);
                    var startup = WindowStartupValues.Default with
                    {
                        Title = "XREngine",
                        X = position.X,
                        Y = position.Y,
                        Width = size.X,
                        Height = size.Y,
                        UseNativeTitleBar = (viewport.Flags & ImGuiViewportFlags.NoDecoration) == 0,
                    };
                    var request = new RuntimeWindowCreateOptions(
                        Startup: startup,
                        GraphicsApi: RuntimeGraphicsApiKind.OpenGL,
                        ResizeStrategy: EInteractiveWindowResizeStrategy.Default,
                        Purpose: RuntimeWindowPurpose.EditorViewport,
                        Position: position,
                        Size: size,
                        VSyncEnabled: false,
                        Visible: false,
                        TopMost: (viewport.Flags & ImGuiViewportFlags.TopMost) != 0,
                        PreferHdrOutput: false,
                        TransparentFramebuffer: false,
                        ColorBits: 32,
                        DepthBits: 24,
                        StencilBits: 8,
                        OpenGlMajorVersion: 4,
                        OpenGlMinorVersion: 6,
                        OpenGlDebugContext: false,
                        OpenGlForwardCompatible: false,
                        SwapAutomatically: false,
                        SharedContext: owner._mainWindow.GlContext);
                    IRuntimeWindowBackend? created = null;
                    try
                    {
                        created = RuntimeWindowBackendRegistry.RequireFactory().Create(in request);
                        created.Initialize(new ViewportWindowEventSink());
                        ImGuiPlatformWindowBehavior.ConfigureNativeWindow(created.OperatingSystemWindowHandle, (uint)viewport.Flags);
                        created.RequestClientScreenPosition(position);
                        IRuntimeWindowGlContext context = created.GlContext
                            ?? throw new InvalidOperationException("An OpenGL editor viewport requires a desktop GL context.");
                        context.MakeCurrent();
                        context.SetSwapInterval(0);
                        Window = created;
                    }
                    catch
                    {
                        try
                        {
                            if (created is not null)
                            {
                                ImGuiPlatformWindowBehavior.ReleaseNativeWindow(created.OperatingSystemWindowHandle);
                                created.Dispose();
                            }
                        }
                        finally
                        {
                            owner._mainWindow.GlContext?.MakeCurrent();
                            _handle.Free();
                        }
                        throw;
                    }
                }

                public IRuntimeWindowBackend Window { get; }
                public uint ViewportId { get; }
                public ImGuiViewportFlags ViewportFlags { get; private set; }
                public bool AcceptsInputs => !ImGuiPlatformWindowBehavior.IsInputTransparent((uint)ViewportFlags);
                public bool Focused => Window.Events.IsFocused;
                public bool IsDisposed => _disposeStarted;
                public nint Handle => GCHandle.ToIntPtr(_handle);

                public void UpdateViewportFlags(ImGuiViewportFlags flags)
                {
                    if (ViewportFlags == flags)
                        return;

                    ViewportFlags = flags;
                    ImGuiPlatformWindowBehavior.ConfigureNativeWindow(Window.OperatingSystemWindowHandle, (uint)flags);
                }

                public bool BeginDispose()
                {
                    if (_disposeStarted)
                        return false;

                    _disposeStarted = true;

                    ImGuiPlatformWindowBehavior.ReleaseNativeWindow(Window.OperatingSystemWindowHandle);

                    try
                    {
                        Window.RequestVisibility(false);
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
                        if (Window.GlContext is { } context)
                        {
                            context.MakeCurrent();
                            _owner._controller?.ReleaseContextResources(context);
                            context.ClearCurrent();
                            _owner._mainWindow.GlContext?.MakeCurrent();
                        }
                        Window.Dispose();
                    }
                    catch
                    {
                        Window.RetainAbandonedResources();
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

                    try
                    {
                        if (Window.GlContext is { } context)
                        {
                            context.MakeCurrent();
                            _owner._controller?.ReleaseContextResources(context);
                            context.ClearCurrent();
                            _owner._mainWindow.GlContext?.MakeCurrent();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogCallbackException("RetireViewportGlContext", ex);
                    }

                    Window.RetainAbandonedResources();
                    AbandonedShutdownWindows.Add(Window);
                }

                public void DrainInput(ImGuiIOPtr io)
                {
                    if (_disposeStarted || _owner._controller is null)
                        return;
                    var origin = Window.ClientScreenPosition;
                    _owner._controller.ReplayViewportInput(
                        Window, origin.X, origin.Y, ViewportId);
                    if (Window.Events.IsFocused)
                        io.MouseHoveredViewport = ViewportId;
                }

                private sealed class ViewportWindowEventSink : IRuntimeWindowEventSink
                {
                    public void SurfaceChanged(WindowSurfaceSnapshot _) { }
                    public void FocusChanged(bool _) { }
                    public void FileDropped(string[] _) { }
                    public void KeyDown(EKey _) { }
                    public bool CloseRequested()
                    {
                        return true;
                    }
                    public void InteractiveResizeStarted() { }
                    public void InteractiveResizeUpdated(IVector2 _) { }
                    public void InteractiveResizeEnded() { }
                    public void RepaintRequested() { }
                    public void RenderRequested(double _) { }
                }
            }
        }
    }
}
