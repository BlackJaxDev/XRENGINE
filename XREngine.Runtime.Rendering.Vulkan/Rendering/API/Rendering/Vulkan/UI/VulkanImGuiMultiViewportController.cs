using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Dear ImGui's platform callbacks and delegates renderer callbacks to one
/// Vulkan surface/swapchain bundle per detached viewport.
/// </summary>
internal sealed unsafe class VulkanImGuiMultiViewportController : IRendererImGuiViewportCallbacks, IDisposable
    {
        private const int PlatformWindowDisposalQuietFrames = 2;
        private static readonly List<IWindow> AbandonedShutdownWindows = [];
        private static readonly List<IInputContext> AbandonedShutdownInputContexts = [];

        private static bool DisposeNativeViewportWindows
            => XREnvironment.IsEnabled(XREngineEnvironmentVariables.ImGuiViewportDisposeNative);

        private readonly IVulkanImGuiOutputHost _outputHost;
        private readonly nint _context;
        private readonly IWindow _mainWindow;
        private readonly Dictionary<uint, VulkanImGuiPlatformWindow> _platformWindows = [];
        private readonly List<PendingPlatformWindowDisposal> _pendingPlatformWindowDisposals = [];
        private readonly List<ImGuiPlatformMonitor> _monitorScratch = [];
        private IDisposable? _callbackRegistration;
        private nint _monitorData;
        private int _monitorCapacity;
        private bool _installed;
        private bool _disposed;
        private bool _deferGpuLifecycle;

        private VulkanImGuiMultiViewportController(IVulkanImGuiOutputHost outputHost, nint context)
        {
            _outputHost = outputHost;
            _context = context;
            _mainWindow = outputHost.MainWindow;
        }

        public static VulkanImGuiMultiViewportController? TryCreate(IVulkanImGuiOutputHost outputHost, nint context)
        {
            if (context == nint.Zero)
            {
                Debug.RenderingWarning("Vulkan ImGui multi-viewports disabled: no ImGui context is available.");
                return null;
            }

            if (!outputHost.TargetRequiresSwapchainOutput || outputHost.MainWindow.VkSurface is null)
            {
                Debug.RenderingWarning("Vulkan ImGui multi-viewports disabled: the renderer does not own a desktop Vulkan surface.");
                return null;
            }

            if (!outputHost.UseDynamicRenderingRenderTargets)
            {
                Debug.RenderingWarning(
                    "Vulkan ImGui multi-viewports disabled: detached windows currently require the Vulkan dynamic-rendering target mode.");
                return null;
            }

            if (!outputHost.IsPlatformOutputReady)
            {
                Debug.RenderingWarning("Vulkan ImGui multi-viewports disabled: Vulkan WSI initialization is incomplete.");
                return null;
            }

            return new VulkanImGuiMultiViewportController(outputHost, context);
        }

        public void Install()
        {
            if (_installed || _disposed)
                return;

            MakeCurrent();
            _callbackRegistration = RendererImGuiViewportCallbackBridge.Register(_context, this);

            ImGuiIOPtr io = ImGui.GetIO();
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            platformIO.NativePtr->Platform_CreateWindow = RendererImGuiViewportCallbackBridge.PlatformCreateWindow;
            platformIO.NativePtr->Platform_DestroyWindow = RendererImGuiViewportCallbackBridge.PlatformDestroyWindow;
            platformIO.NativePtr->Platform_ShowWindow = RendererImGuiViewportCallbackBridge.PlatformShowWindow;
            platformIO.NativePtr->Platform_SetWindowPos = RendererImGuiViewportCallbackBridge.PlatformSetWindowPosition;
            ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowPos(
                platformIO.NativePtr,
                RendererImGuiViewportCallbackBridge.PlatformGetWindowPosition);
            platformIO.NativePtr->Platform_SetWindowSize = RendererImGuiViewportCallbackBridge.PlatformSetWindowSize;
            ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowSize(
                platformIO.NativePtr,
                RendererImGuiViewportCallbackBridge.PlatformGetWindowSize);
            platformIO.NativePtr->Platform_SetWindowFocus = RendererImGuiViewportCallbackBridge.PlatformSetWindowFocus;
            platformIO.NativePtr->Platform_GetWindowFocus = RendererImGuiViewportCallbackBridge.PlatformGetWindowFocus;
            platformIO.NativePtr->Platform_GetWindowMinimized = RendererImGuiViewportCallbackBridge.PlatformGetWindowMinimized;
            platformIO.NativePtr->Platform_SetWindowTitle = RendererImGuiViewportCallbackBridge.PlatformSetWindowTitle;
            platformIO.NativePtr->Platform_SetWindowAlpha = RendererImGuiViewportCallbackBridge.PlatformSetWindowAlpha;
            platformIO.NativePtr->Platform_UpdateWindow = RendererImGuiViewportCallbackBridge.PlatformUpdateWindow;
            platformIO.NativePtr->Platform_RenderWindow = RendererImGuiViewportCallbackBridge.PlatformRenderWindow;
            platformIO.NativePtr->Platform_SwapBuffers = RendererImGuiViewportCallbackBridge.PlatformSwapBuffers;
            platformIO.NativePtr->Platform_GetWindowDpiScale = RendererImGuiViewportCallbackBridge.PlatformGetWindowDpiScale;
            platformIO.NativePtr->Platform_OnChangedViewport = RendererImGuiViewportCallbackBridge.PlatformOnChangedViewport;
            platformIO.NativePtr->Renderer_CreateWindow = RendererImGuiViewportCallbackBridge.RendererCreateWindow;
            platformIO.NativePtr->Renderer_DestroyWindow = RendererImGuiViewportCallbackBridge.RendererDestroyWindow;
            platformIO.NativePtr->Renderer_SetWindowSize = RendererImGuiViewportCallbackBridge.RendererSetWindowSize;
            platformIO.NativePtr->Renderer_RenderWindow = RendererImGuiViewportCallbackBridge.RendererRenderWindow;
            platformIO.NativePtr->Renderer_SwapBuffers = RendererImGuiViewportCallbackBridge.RendererSwapBuffers;

            EnsureMainViewportPlatformData();
            UpdatePlatformMonitors();
            io.BackendFlags |=
                ImGuiBackendFlags.PlatformHasViewports |
                ImGuiBackendFlags.RendererHasViewports |
                ImGuiBackendFlags.HasMouseHoveredViewport;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
            PrepareImplicitWindowForNewFrame();
            _installed = true;

            Debug.Rendering("Vulkan ImGui multi-viewports enabled.");
        }

        public void PrepareForNewFrame(ImGuiIOPtr io)
        {
            if (!_installed || _disposed)
                return;

            MakeCurrent();
            PrepareImplicitWindowForNewFrame();
            EnsureMainViewportPlatformData();

            if (!OperatingSystem.IsWindows() || !GetCursorPos(out NativePoint cursorPosition))
                return;

            uint viewportId = ResolveHoveredViewportId(cursorPosition);
            io.AddMousePosEvent(cursorPosition.X, cursorPosition.Y);
            io.AddMouseViewportEvent(viewportId);
            io.MouseHoveredViewport = viewportId;
        }

        public void UpdatePlatformWindows(bool deferGpuLifecycle)
        {
            if (!_installed || _disposed)
                return;

            MakeCurrent();
            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
                return;

            try
            {
                // Runtime-close cleanup destroys WSI resources and may wait for
                // queue completion. Retain those resources until an ordinary
                // desktop frame owns the optional-output budget.
                _deferGpuLifecycle = deferGpuLifecycle;
                if (!deferGpuLifecycle)
                    DisposePendingPlatformWindows();
                UpdatePlatformMonitors();
                ImGui.UpdatePlatformWindows();
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(UpdatePlatformWindows), ex);
            }
            finally
            {
                _deferGpuLifecycle = false;
            }
        }

        public void RenderPlatformWindows()
        {
            if (!_installed || _disposed)
                return;

            MakeCurrent();
            if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
                return;

            try
            {
                ImGui.RenderPlatformWindowsDefault();
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(RenderPlatformWindows), ex);
            }
        }

        public void RenderPendingViewports()
        {
            if (!_installed || _disposed)
                return;

            foreach (VulkanImGuiPlatformWindow window in _platformWindows.Values)
            {
                if (window.IsDisposed)
                    continue;

                try
                {
                    window.RenderPending();
                    if (window.RendererReady)
                        ShowPlatformWindow(window.Window);
                }
                catch (Exception ex)
                {
                    LogCallbackException($"RenderViewport[0x{window.ViewportId:X8}]", ex);
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
                MakeCurrent();
                if (_installed && ImGuiContextTracker.IsAlive(_context))
                    ImGui.DestroyPlatformWindows();

                ClearPlatformMonitors();
                ClearPlatformCallbacks();

                ImGuiIOPtr io = ImGui.GetIO();
                io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
                io.BackendFlags &= ~(
                    ImGuiBackendFlags.PlatformHasViewports |
                    ImGuiBackendFlags.RendererHasViewports |
                    ImGuiBackendFlags.HasMouseHoveredViewport);
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(Dispose), ex);
            }

            foreach (VulkanImGuiPlatformWindow window in _platformWindows.Values)
            {
                window.DestroyRendererResources();
                window.AbandonNativeWindowForShutdown();
            }
            _platformWindows.Clear();
            AbandonPendingPlatformWindowsForShutdown();
            Interlocked.Exchange(ref _callbackRegistration, null)?.Dispose();
        }

        private void MakeCurrent()
        {
            if (ImGuiContextTracker.IsAlive(_context))
                ImGui.SetCurrentContext(_context);
        }

        private static void PrepareImplicitWindowForNewFrame()
            => ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);

        private void EnsureMainViewportPlatformData()
        {
            ImGuiViewportPtr mainViewport = ImGui.GetMainViewport();
            mainViewport.PlatformHandle = _mainWindow.Handle;
            mainViewport.PlatformHandleRaw = _mainWindow.Handle;
            Vector2D<int> clientPosition = GetClientScreenPosition(_mainWindow);
            mainViewport.Pos = new Vector2(clientPosition.X, clientPosition.Y);
            mainViewport.DpiScale = GetWindowDpiScale(_mainWindow);
        }

        private VulkanImGuiPlatformWindow? GetPlatformWindow(ImGuiViewportPtr viewport)
        {
            if (viewport.PlatformUserData != nint.Zero)
            {
                try
                {
                    if (GCHandle.FromIntPtr(viewport.PlatformUserData).Target is VulkanImGuiPlatformWindow window)
                        return window;
                }
                catch
                {
                }
            }

            // A restored viewport can reach renderer/show callbacks with platform
            // user data temporarily cleared. The viewport ID is the core-owned,
            // stable identity for the entire callback sequence.
            return _platformWindows.TryGetValue(viewport.ID, out VulkanImGuiPlatformWindow? registered)
                ? registered
                : null;
        }

        private IWindow GetWindow(ImGuiViewportPtr viewport)
            => GetPlatformWindow(viewport)?.Window ?? _mainWindow;

        private void PlatformCreateWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                if (_platformWindows.ContainsKey(viewport.ID))
                    return;

                VulkanImGuiPlatformWindow window = new(this, _outputHost, viewport);
                _platformWindows.Add(viewport.ID, window);
                viewport.PlatformUserData = window.Handle;
                viewport.PlatformHandle = window.Window.Handle;
                viewport.PlatformHandleRaw = window.Window.Handle;
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformCreateWindow), ex);
                new ImGuiViewportPtr(nativeViewport).PlatformRequestClose = true;
            }
        }

        private void PlatformDestroyWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                VulkanImGuiPlatformWindow? window = GetPlatformWindow(viewport);
                viewport.PlatformUserData = nint.Zero;
                viewport.PlatformHandle = nint.Zero;
                viewport.PlatformHandleRaw = nint.Zero;

                if (window is null)
                    return;

                _platformWindows.Remove(window.ViewportId);
                QueuePlatformWindowDispose(window);
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
                    ShowPlatformWindow(window.Window);
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformShowWindow), ex);
            }
        }

        private void PlatformSetWindowPosition(ImGuiViewport* nativeViewport, Vector2 position)
        {
            try
            {
                GetPlatformWindow(new ImGuiViewportPtr(nativeViewport))?.SetPosition(ToWindowPosition(position));
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformSetWindowPosition), ex);
            }
        }

        private void PlatformGetWindowPosition(ImGuiViewport* nativeViewport, Vector2* outPosition)
        {
            try
            {
                Vector2D<int> position = GetClientScreenPosition(GetWindow(new ImGuiViewportPtr(nativeViewport)));
                *outPosition = new Vector2(position.X, position.Y);
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformGetWindowPosition), ex);
                *outPosition = Vector2.Zero;
            }
        }

        private void PlatformSetWindowSize(ImGuiViewport* nativeViewport, Vector2 size)
        {
            try
            {
                GetPlatformWindow(new ImGuiViewportPtr(nativeViewport))?.SetSize(ToWindowSize(size));
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
                VulkanImGuiPlatformWindow? window = GetPlatformWindow(new ImGuiViewportPtr(nativeViewport));
                return (window?.Focused ?? _outputHost.MainWindowFocused) ? (byte)1 : (byte)0;
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformGetWindowFocus), ex);
                return 0;
            }
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

        private static void PlatformSetWindowAlpha(ImGuiViewport* nativeViewport, float alpha)
        {
            // Silk.NET.Windowing does not expose cross-platform native window opacity.
        }

        private void PlatformUpdateWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                GetPlatformWindow(viewport)?.ProcessEvents(viewport);
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
                return GetWindowDpiScale(GetWindow(new ImGuiViewportPtr(nativeViewport)));
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(PlatformGetWindowDpiScale), ex);
                return 1.0f;
            }
        }

        private static void PlatformOnChangedViewport(ImGuiViewport* nativeViewport)
        {
        }

        private static void PlatformRenderWindow(ImGuiViewport* nativeViewport, void* renderArgument)
        {
            // Vulkan rendering is recorded by Renderer_RenderWindow and submitted after the
            // primary scene submission so detached viewports observe completed engine textures.
        }

        private static void PlatformSwapBuffers(ImGuiViewport* nativeViewport, void* renderArgument)
        {
        }

        private void RendererCreateWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                if (GetPlatformWindow(viewport) is not { } window)
                    return;

                viewport.RendererUserData = window.Handle;
                if (_deferGpuLifecycle)
                    return;

                window.CreateRendererResources();

                // Restored INI viewports do not consistently receive a later
                // Platform_ShowWindow callback. Reveal the window only after its
                // swapchain is ready so saved detached layouts neither stay hidden
                // nor flash an uninitialized surface.
                ShowPlatformWindow(window.Window);
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(RendererCreateWindow), ex);
                new ImGuiViewportPtr(nativeViewport).PlatformRequestClose = true;
            }
        }

        private void RendererDestroyWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                // PlatformDestroyWindow queues the complete native/renderer
                // bundle for completion-safe retirement. Destroying here would
                // synchronously wait for queues inside ImGui.UpdatePlatformWindows.
                viewport.RendererUserData = nint.Zero;
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(RendererDestroyWindow), ex);
            }
        }

        private void RendererSetWindowSize(ImGuiViewport* nativeViewport, Vector2 size)
        {
            try
            {
                GetPlatformWindow(new ImGuiViewportPtr(nativeViewport))?.RequestRendererResize();
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(RendererSetWindowSize), ex);
            }
        }

        private void RendererRenderWindow(ImGuiViewport* nativeViewport, void* renderArgument)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                if (viewport.DrawData.NativePtr is null)
                    return;

                GetPlatformWindow(viewport)?.CaptureDrawData(viewport.DrawData);
            }
            catch (Exception ex)
            {
                LogCallbackException(nameof(RendererRenderWindow), ex);
            }
        }

        private static void RendererSwapBuffers(ImGuiViewport* nativeViewport, void* renderArgument)
        {
        }

        private void QueuePlatformWindowDispose(VulkanImGuiPlatformWindow window)
        {
            if (!window.BeginDispose())
                return;

            _pendingPlatformWindowDisposals.Add(new PendingPlatformWindowDisposal(window));
        }

        private void DisposePendingPlatformWindows(bool force = false)
        {
            int writeIndex = 0;
            for (int i = 0; i < _pendingPlatformWindowDisposals.Count; i++)
            {
                PendingPlatformWindowDisposal pending = _pendingPlatformWindowDisposals[i];
                if (!force && pending.QuietFramesRemaining-- > 0)
                {
                    _pendingPlatformWindowDisposals[writeIndex++] = pending;
                    continue;
                }

                try
                {
                    pending.Window.ReleaseAfterRuntimeClose();
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(DisposePendingPlatformWindows), ex);
                    pending.Window.AbandonNativeWindowForShutdown();
                }
            }

            if (writeIndex < _pendingPlatformWindowDisposals.Count)
                _pendingPlatformWindowDisposals.RemoveRange(writeIndex, _pendingPlatformWindowDisposals.Count - writeIndex);
        }

        private void AbandonPendingPlatformWindowsForShutdown()
        {
            foreach (PendingPlatformWindowDisposal pending in _pendingPlatformWindowDisposals)
                pending.Window.AbandonNativeWindowForShutdown();
            _pendingPlatformWindowDisposals.Clear();
        }

        private uint ResolveHoveredViewportId(NativePoint screenPosition)
        {
            foreach (VulkanImGuiPlatformWindow window in _platformWindows.Values)
            {
                if (!window.IsDisposed &&
                    TryGetWindowScreenRect(window.Window, out NativeRect rect) &&
                    rect.Contains(screenPosition))
                {
                    return window.ViewportId;
                }
            }

            if (TryGetWindowScreenRect(_mainWindow, out NativeRect mainRect) && mainRect.Contains(screenPosition))
                return ImGui.GetMainViewport().ID;

            return 0;
        }

        internal void RequestClose(uint viewportId)
        {
            MakeCurrent();
            ImGuiViewport* viewport = ImGuiNative.igFindViewportByID(viewportId);
            if (viewport is not null)
                new ImGuiViewportPtr(viewport).PlatformRequestClose = true;
        }

        internal void PushMousePosition(uint viewportId, IWindow window, Vector2 localPosition)
        {
            MakeCurrent();
            Vector2D<int> clientPosition = GetClientScreenPosition(window);
            ImGuiIOPtr io = ImGui.GetIO();
            io.AddMousePosEvent(clientPosition.X + localPosition.X, clientPosition.Y + localPosition.Y);
            io.AddMouseViewportEvent(viewportId);
        }

        internal void PushMouseButton(uint viewportId, MouseButton button, bool down)
        {
            if (!VulkanImGuiInputRouter.TryConvertMouseButton(button, out int imGuiButton))
                return;

            MakeCurrent();
            ImGuiIOPtr io = ImGui.GetIO();
            io.AddMouseButtonEvent(imGuiButton, down);
            io.AddMouseViewportEvent(viewportId);
        }

        internal void PushMouseWheel(uint viewportId, ScrollWheel wheel)
        {
            MakeCurrent();
            ImGuiIOPtr io = ImGui.GetIO();
            io.AddMouseWheelEvent(wheel.X, wheel.Y);
            io.AddMouseViewportEvent(viewportId);
        }

        internal void PushKey(IKeyboard keyboard, Key key, bool down)
        {
            if (!VulkanImGuiInputRouter.TryConvertKey(key, out ImGuiKey imGuiKey))
                return;

            MakeCurrent();
            ImGuiIOPtr io = ImGui.GetIO();
            io.AddKeyEvent(imGuiKey, down);
            io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
            io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight));
            io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));
            io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
        }

        internal void PushChar(char value)
        {
            MakeCurrent();
            ImGui.GetIO().AddInputCharacter(value);
        }

        private void UpdatePlatformMonitors()
        {
            _monitorScratch.Clear();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    EnumDisplayMonitors(
                        nint.Zero,
                        nint.Zero,
                        RendererImGuiViewportCallbackBridge.MonitorEnumeration,
                        nint.Zero);
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

        private bool EnumerateMonitor(nint monitor)
        {
            NativeMonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return true;

            _monitorScratch.Add(new ImGuiPlatformMonitor
            {
                MainPos = new Vector2(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top),
                MainSize = new Vector2(monitorInfo.Monitor.Width, monitorInfo.Monitor.Height),
                WorkPos = new Vector2(monitorInfo.Work.Left, monitorInfo.Work.Top),
                WorkSize = new Vector2(monitorInfo.Work.Width, monitorInfo.Work.Height),
                DpiScale = GetMonitorDpiScale(monitor),
                PlatformHandle = (void*)monitor,
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
            });
        }

        private void WritePlatformMonitorBuffer()
        {
            int count = _monitorScratch.Count;
            if (count > _monitorCapacity)
            {
                if (_monitorData != nint.Zero)
                    Marshal.FreeHGlobal(_monitorData);
                _monitorData = Marshal.AllocHGlobal(sizeof(ImGuiPlatformMonitor) * count);
                _monitorCapacity = count;
            }

            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            MutableImVector* monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
            monitors->Size = count;
            monitors->Capacity = _monitorCapacity;
            monitors->Data = _monitorData;

            ImGuiPlatformMonitor* destination = (ImGuiPlatformMonitor*)_monitorData;
            for (int i = 0; i < count; i++)
                destination[i] = _monitorScratch[i];
        }

        private void ClearPlatformMonitors()
        {
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
            MutableImVector* monitors = (MutableImVector*)&platformIO.NativePtr->Monitors;
            monitors->Size = 0;
            monitors->Capacity = 0;
            monitors->Data = nint.Zero;

            if (_monitorData != nint.Zero)
                Marshal.FreeHGlobal(_monitorData);
            _monitorData = nint.Zero;
            _monitorCapacity = 0;
        }

        private void ClearPlatformCallbacks()
        {
            ImGuiPlatformIOPtr platformIO = ImGui.GetPlatformIO();
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

        private static Vector2D<int> ToWindowSize(Vector2 size)
            => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

        private static Vector2D<int> ToWindowPosition(Vector2 position)
            => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

        internal static Vector2D<int> GetClientScreenPosition(IWindow window)
        {
            if (OperatingSystem.IsWindows() && window.Handle != nint.Zero)
            {
                NativePoint point = default;
                if (ClientToScreen(window.Handle, ref point))
                    return new Vector2D<int>(point.X, point.Y);
            }

            return window.Position;
        }

        internal static void SetClientScreenPosition(IWindow window, Vector2D<int> targetClientPosition)
        {
            Vector2D<int> clientPosition = GetClientScreenPosition(window);
            Vector2D<int> clientOffset = clientPosition - window.Position;
            window.Position = targetClientPosition - clientOffset;
        }

        private static void ShowPlatformWindow(IWindow window)
        {
            window.IsVisible = true;

            // GLFW's Vulkan window visibility setter can leave a restored window
            // native-hidden on Windows. Preserve ImGui's no-focus-on-appearing
            // behavior while making the platform callback authoritative.
            if (OperatingSystem.IsWindows() &&
                window.Handle != nint.Zero &&
                !IsWindowVisible(window.Handle))
            {
                _ = ShowWindowNative(window.Handle, ShowWindowWithoutActivation);
            }
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
                Bottom = position.Y + Math.Max(1, size.Y),
            };
            return true;
        }

        private static float GetWindowDpiScale(IWindow window)
        {
            Vector2D<int> size = window.Size;
            Vector2D<int> framebufferSize = window.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0)
                return 1.0f;

            float scale = MathF.Max(framebufferSize.X / (float)size.X, framebufferSize.Y / (float)size.Y);
            return float.IsFinite(scale) && scale > 0.0f && scale < 99.0f ? scale : 1.0f;
        }

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

        private static void LogCallbackException(string callback, Exception ex)
        {
            Debug.RenderingWarningEvery(
                $"Vulkan.ImGui.MultiViewport.{callback}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.ImGuiMultiViewport] {0} failed: {1}",
                callback,
                ex.Message);
        }

        void IRendererImGuiViewportCallbacks.PlatformCreateWindow(nint viewport)
            => PlatformCreateWindow((ImGuiViewport*)viewport);
        void IRendererImGuiViewportCallbacks.PlatformDestroyWindow(nint viewport)
            => PlatformDestroyWindow((ImGuiViewport*)viewport);
        void IRendererImGuiViewportCallbacks.PlatformShowWindow(nint viewport)
            => PlatformShowWindow((ImGuiViewport*)viewport);
        void IRendererImGuiViewportCallbacks.PlatformSetWindowPosition(nint viewport, Vector2 value)
            => PlatformSetWindowPosition((ImGuiViewport*)viewport, value);
        void IRendererImGuiViewportCallbacks.PlatformGetWindowPosition(nint viewport, nint value)
            => PlatformGetWindowPosition((ImGuiViewport*)viewport, (Vector2*)value);
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
            => EnumerateMonitor(monitor) ? 1 : 0;

        internal struct PendingPlatformWindowDisposal(VulkanImGuiPlatformWindow window)
        {
            public VulkanImGuiPlatformWindow Window = window;
            public int QuietFramesRemaining = PlatformWindowDisposalQuietFrames;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MutableImVector
        {
            public int Size;
            public int Capacity;
            public nint Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public readonly int Width => Right - Left;
            public readonly int Height => Bottom - Top;
            public readonly bool Contains(NativePoint point)
                => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct NativeMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        internal enum MonitorDpiType
        {
            Effective = 0,
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(nint hWnd);
        [DllImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
        private static extern bool ShowWindowNative(nint hWnd, int command);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(nint hdc, nint clipRect, nint callback, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo monitorInfo);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

        private const int ShowWindowWithoutActivation = 4;

        internal static void PreserveAbandonedWindow(IWindow window, IInputContext? input)
        {
            if (input is not null)
                AbandonedShutdownInputContexts.Add(input);
            AbandonedShutdownWindows.Add(window);
        }

        internal static bool ShouldDisposeNativeWindow => DisposeNativeViewportWindows;
    }
