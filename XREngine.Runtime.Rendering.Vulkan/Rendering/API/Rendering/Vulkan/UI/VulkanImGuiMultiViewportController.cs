using ImGuiNET;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.UI;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Dear ImGui's platform callbacks and delegates renderer callbacks to one
/// Vulkan surface/swapchain bundle per detached viewport.
/// </summary>
internal sealed unsafe class VulkanImGuiMultiViewportController : IRendererImGuiViewportCallbacks, IDisposable
    {
        private const int PlatformWindowDisposalQuietFrames = 2;
        private const int MaximumRetainedPlatformWindowRetirees = 8;

        private static bool DisposeNativeViewportWindows
            => XREnvironment.IsEnabled(XREngineEnvironmentVariables.ImGuiViewportDisposeNative);

        private readonly IVulkanImGuiOutputHost _outputHost;
        private readonly nint _context;
        private readonly IRuntimeWindowBackend _mainWindow;
        private readonly Dictionary<uint, VulkanImGuiPlatformWindow> _platformWindows = [];
        private readonly List<PendingPlatformWindowDisposal> _pendingPlatformWindowDisposals = [];
        private readonly List<ImGuiPlatformMonitor> _monitorScratch = [];
        private IDisposable? _callbackRegistration;
        private nint _monitorData;
        private int _monitorCapacity;
        private bool _installed;
        private bool _disposed;
        private bool _deferGpuLifecycle;
        private bool _retirementBackpressureReported;
        private bool _leftCtrl, _rightCtrl, _leftShift, _rightShift;
        private bool _leftAlt, _rightAlt, _leftSuper, _rightSuper;

        private VulkanImGuiMultiViewportController(IVulkanImGuiOutputHost outputHost, nint context)
        {
            _outputHost = outputHost;
            _context = context;
            _mainWindow = outputHost.MainWindow;
        }

        public static VulkanImGuiMultiViewportController? TryCreate(IVulkanImGuiOutputHost outputHost, nint context)
        {
            IRuntimeWindowBackend mainWindow = outputHost.MainWindow;
            if (mainWindow.OwnerThreadId != Environment.CurrentManagedThreadId)
            {
                Debug.RenderingWarning(
                    "Vulkan ImGui multi-viewports require a collapsed desktop window/render owner; synchronous platform callbacks are unavailable on this split topology.");
                return null;
            }

            if (context == nint.Zero)
            {
                Debug.RenderingWarning("Vulkan ImGui multi-viewports disabled: no ImGui context is available.");
                return null;
            }

            if (!outputHost.TargetRequiresSwapchainOutput || mainWindow.VulkanSurface is null)
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

            foreach (VulkanImGuiPlatformWindow window in _platformWindows.Values)
                window.DrainInput();

            if (!_mainWindow.Events.IsFocused)
                return;

            WindowInputSnapshot input = _mainWindow.Input;
            if (!input.HasMouse)
                return;
            IVector2 origin = _mainWindow.ClientScreenPosition;
            IVector2 cursorPosition = new(origin.X + (int)input.PointerX, origin.Y + (int)input.PointerY);
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
                // Retirement only polls per-window proof objects and therefore
                // must continue even when this frame defers new GPU lifecycle
                // work. Closing windows otherwise retain their native surfaces
                // forever while minimized or hidden.
                _deferGpuLifecycle = deferGpuLifecycle;
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
                        ShowPlatformWindow(window, window.ViewportFlags);
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
            mainViewport.PlatformHandle = _mainWindow.PlatformWindowHandle;
            mainViewport.PlatformHandleRaw = _mainWindow.OperatingSystemWindowHandle;
            IVector2 clientPosition = GetClientScreenPosition(_mainWindow);
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

        private IRuntimeWindowBackend GetWindow(ImGuiViewportPtr viewport)
        {
            if (viewport.ID == ImGui.GetMainViewport().ID)
                return _mainWindow;
            return GetPlatformWindow(viewport)?.Window
                ?? throw new InvalidOperationException($"ImGui viewport {viewport.ID} has no desktop window.");
        }

        private void PlatformCreateWindow(ImGuiViewport* nativeViewport)
        {
            try
            {
                ImGuiViewportPtr viewport = new(nativeViewport);
                if (_platformWindows.ContainsKey(viewport.ID))
                    return;
                if (_pendingPlatformWindowDisposals.Count >= MaximumRetainedPlatformWindowRetirees)
                {
                    viewport.PlatformRequestClose = true;
                    ReportPlatformWindowRetirementBackpressure();
                    return;
                }

                _retirementBackpressureReported = false;

                VulkanImGuiPlatformWindow window = new(this, _outputHost, viewport);
                _platformWindows.Add(viewport.ID, window);
                viewport.PlatformUserData = window.Handle;
                viewport.PlatformHandle = window.Window.PlatformWindowHandle;
                viewport.PlatformHandleRaw = window.Window.OperatingSystemWindowHandle;
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
                var viewport = new ImGuiViewportPtr(nativeViewport);
                if (GetPlatformWindow(viewport) is { } window)
                    ShowPlatformWindow(window, viewport.Flags);
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
                IVector2 position = GetClientScreenPosition(GetWindow(new ImGuiViewportPtr(nativeViewport)));
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

        private static void PlatformSetWindowAlpha(ImGuiViewport* nativeViewport, float alpha)
        {
            // Native window opacity is optional and the desktop backend does not expose it.
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
                ShowPlatformWindow(window, viewport.Flags);
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
                    if (!pending.Window.TryReleaseAfterRuntimeClose())
                    {
                        _pendingPlatformWindowDisposals[writeIndex++] = pending;
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    LogCallbackException(nameof(DisposePendingPlatformWindows), ex);
                    // A failed proof must retain the native surface. Retrying
                    // after a short quiet period avoids a hot exception loop
                    // without converting an indeterminate WSI release into an
                    // unsafe destruction.
                    pending.QuietFramesRemaining = PlatformWindowDisposalQuietFrames;
                    _pendingPlatformWindowDisposals[writeIndex++] = pending;
                }
            }

            if (writeIndex < _pendingPlatformWindowDisposals.Count)
                _pendingPlatformWindowDisposals.RemoveRange(writeIndex, _pendingPlatformWindowDisposals.Count - writeIndex);

            if (_pendingPlatformWindowDisposals.Count < MaximumRetainedPlatformWindowRetirees)
                _retirementBackpressureReported = false;
        }

        private void ReportPlatformWindowRetirementBackpressure()
        {
            if (_retirementBackpressureReported)
                return;

            _retirementBackpressureReported = true;
            Debug.RenderingWarning(
                "[Vulkan.ImGuiMultiViewport] Refusing detached viewport creation because {0} retired windows are still awaiting WSI release proof.",
                MaximumRetainedPlatformWindowRetirees);
        }

        private void AbandonPendingPlatformWindowsForShutdown()
        {
            foreach (PendingPlatformWindowDisposal pending in _pendingPlatformWindowDisposals)
                pending.Window.AbandonNativeWindowForShutdown();
            _pendingPlatformWindowDisposals.Clear();
        }

        private uint ResolveHoveredViewportId(IVector2 screenPosition)
        {
            foreach (VulkanImGuiPlatformWindow window in _platformWindows.Values)
            {
                if (!window.IsDisposed &&
                    window.AcceptsInputs &&
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

        internal void ReplayViewportInput(
            uint viewportId,
            IVector2 screenOrigin,
            WindowInputSnapshot input,
            ReadOnlySpan<WindowInputEvent> events)
        {
            MakeCurrent();
            ImGuiIOPtr io = ImGui.GetIO();
            for (int i = 0; i < events.Length; i++)
            {
                WindowInputEvent item = events[i];
                switch (item.Kind)
                {
                    case WindowInputEventKind.Key:
                        if (VulkanImGuiInputRouter.TryConvertKey(item.Key, out ImGuiKey key))
                            io.AddKeyEvent(key, item.IsDown);
                        ReplayModifier(io, item.Key, item.IsDown);
                        break;
                    case WindowInputEventKind.MouseButton:
                        io.AddMouseButtonEvent((int)item.MouseButton, item.IsDown);
                        io.AddMouseViewportEvent(viewportId);
                        break;
                    case WindowInputEventKind.Text:
                        io.AddInputCharacter(item.Character);
                        break;
                    case WindowInputEventKind.Pointer:
                        io.AddMousePosEvent(screenOrigin.X + item.X, screenOrigin.Y + item.Y);
                        io.AddMouseViewportEvent(viewportId);
                        break;
                    case WindowInputEventKind.Scroll:
                        io.AddMouseWheelEvent(item.X, item.Y);
                        io.AddMouseViewportEvent(viewportId);
                        break;
                }
            }
            if (input.IsFocused)
                io.AddFocusEvent(true);
        }

        private void ReplayModifier(ImGuiIOPtr io, EKey key, bool down)
        {
            switch (key)
            {
                case EKey.ControlLeft: _leftCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
                case EKey.ControlRight: _rightCtrl = down; io.AddKeyEvent(ImGuiKey.ModCtrl, _leftCtrl || _rightCtrl); break;
                case EKey.ShiftLeft: _leftShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
                case EKey.ShiftRight: _rightShift = down; io.AddKeyEvent(ImGuiKey.ModShift, _leftShift || _rightShift); break;
                case EKey.AltLeft: _leftAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
                case EKey.AltRight: _rightAlt = down; io.AddKeyEvent(ImGuiKey.ModAlt, _leftAlt || _rightAlt); break;
                case EKey.WinLeft: _leftSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
                case EKey.WinRight: _rightSuper = down; io.AddKeyEvent(ImGuiKey.ModSuper, _leftSuper || _rightSuper); break;
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

        private static IVector2 ToWindowSize(Vector2 size)
            => new(Math.Max(1, (int)MathF.Round(size.X)), Math.Max(1, (int)MathF.Round(size.Y)));

        private static IVector2 ToWindowPosition(Vector2 position)
            => new((int)MathF.Round(position.X), (int)MathF.Round(position.Y));

        internal static IVector2 GetClientScreenPosition(IRuntimeWindowBackend window)
            => window.ClientScreenPosition;

        private static void ShowPlatformWindow(VulkanImGuiPlatformWindow platformWindow, ImGuiViewportFlags flags)
        {
            platformWindow.UpdateViewportFlags(flags);
            IRuntimeWindowBackend window = platformWindow.Window;
            if (!ImGuiPlatformWindowBehavior.TryShowWithoutActivation(window.OperatingSystemWindowHandle, (uint)flags))
                window.RequestVisibility(true);
        }

        private static bool TryGetWindowScreenRect(IRuntimeWindowBackend window, out NativeRect rect)
        {
            IVector2 position = GetClientScreenPosition(window);
            WindowSurfaceSnapshot surface = window.Surface;
            rect = new NativeRect
            {
                Left = position.X,
                Top = position.Y,
                Right = position.X + Math.Max(1, surface.ClientWidth),
                Bottom = position.Y + Math.Max(1, surface.ClientHeight),
            };
            return true;
        }

        private static float GetWindowDpiScale(IRuntimeWindowBackend window)
        {
            WindowSurfaceSnapshot surface = window.Surface;
            float scale = MathF.Max(surface.DpiScaleX, surface.DpiScaleY);
            return float.IsFinite(scale) && scale > 0.0f && scale < 99.0f ? scale : 1.0f;
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
            => 1;

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
        internal struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public readonly int Width => Right - Left;
            public readonly int Height => Bottom - Top;
            public readonly bool Contains(IVector2 point)
                => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
        }

        internal static bool ShouldDisposeNativeWindow => DisposeNativeViewportWindows;
    }
