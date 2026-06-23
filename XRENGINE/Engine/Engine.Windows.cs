using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Threading;
using XREngine.Rendering;

namespace XREngine
{
    /// <summary>
    /// Window and viewport management for the engine.
    /// </summary>
    public static partial class Engine
    {
        #region Window Management

        /// <summary>
        /// Creates windows from a list of startup settings.
        /// </summary>
        /// <param name="windows">The list of window configurations to create.</param>
        public static void CreateWindows(List<GameWindowStartupSettings> windows)
        {
            bool splitWindowPumpStarted = WindowPumpHost.TryStartForStartupWindows(windows);
            if (splitWindowPumpStarted)
            {
                SetRenderThreadId(0);
                Debug.Rendering(
                    "[RenderThreadHost] Dedicated render thread assignment is pending. WindowThreadId={0}.",
                    WindowThreadId);
            }

            foreach (var windowSettings in windows)
                CreateWindow(windowSettings);

            WaitForStartupWindowAttachment(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Creates a new engine window with the specified settings.
        /// </summary>
        /// <param name="windowSettings">Configuration for the window.</param>
        /// <returns>The created window instance.</returns>
        /// <remarks>
        /// If Vulkan initialization fails, automatically falls back to OpenGL in the collapsed
        /// window/render path. The experimental split window pump path keeps Vulkan explicit.
        /// </remarks>
        public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
        {
            bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
            EInteractiveWindowResizeStrategy interactiveResizeStrategy = ResolveInteractiveResizeStrategy(windowSettings);
            bool useNativeTitleBar = windowSettings.UseNativeTitleBar &&
                interactiveResizeStrategy != EInteractiveWindowResizeStrategy.EngineBorderlessResize;
            var options = GetWindowOptions(windowSettings, preferHdrOutput, interactiveResizeStrategy);

            Debug.Rendering(
                "[StartupWindow] Creating window '{0}' state={1} pos=({2},{3}) size={4}x{5} api={6} targetWorld={7} interactiveResize={8} currentThread={9} windowThread={10} renderThread={11}",
                windowSettings.WindowTitle ?? string.Empty,
                windowSettings.WindowState,
                windowSettings.X,
                windowSettings.Y,
                windowSettings.Width,
                windowSettings.Height,
                options.API.API,
                windowSettings.TargetWorld?.Name ?? "<null>",
                interactiveResizeStrategy,
                Environment.CurrentManagedThreadId,
                WindowThreadId,
                RenderThreadId);

            bool createOnWindowPumpHost = WindowPumpHost.ShouldCreateWindowOnHost(windowSettings);
            XRWindow window = createOnWindowPumpHost
                ? WindowPumpHost.CreateWindow(
                    () => CreateWindowInstance(
                        options,
                        useNativeTitleBar,
                        windowSettings.VSync,
                        interactiveResizeStrategy,
                        allowVulkanFallback: false),
                    $"CreateWindow[{windowSettings.WindowTitle ?? string.Empty}]")
                : CreateWindowInstance(
                    options,
                    useNativeTitleBar,
                    windowSettings.VSync,
                    interactiveResizeStrategy,
                    allowVulkanFallback: true);

            FinishWindowCreation(windowSettings, window, preferHdrOutput);

            return window;
        }

        private static XRWindow CreateWindowInstance(
            WindowOptions options,
            bool useNativeTitleBar,
            bool windowVSyncRequested,
            EInteractiveWindowResizeStrategy interactiveResizeStrategy,
            bool allowVulkanFallback)
        {
            try
            {
                return new XRWindow(options, useNativeTitleBar, windowVSyncRequested, interactiveResizeStrategy);
            }
            catch (Exception ex) when (allowVulkanFallback && options.API.API == ContextAPI.Vulkan)
            {
                Debug.RenderingWarning($"Vulkan initialization failed, falling back to OpenGL: {ex.Message}");
                options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ResolveOpenGLContextFlags(), new APIVersion(4, 6));
                return new XRWindow(options, useNativeTitleBar, windowVSyncRequested, interactiveResizeStrategy);
            }
        }

        private static void FinishWindowCreation(
            GameWindowStartupSettings windowSettings,
            XRWindow window,
            bool preferHdrOutput)
        {
            window.PreferHDROutput = preferHdrOutput;
            CreateViewports(windowSettings.LocalPlayers, window);
            window.UpdateViewportSizes();
            _windows.Add(window);
            window.ApplyVSyncMode(EffectiveSettings.VSync);

            Vector2D<int> framebufferSize = window.EffectiveFramebufferSize;
            Debug.Rendering(
                "[StartupWindow] Window created hash={0} framebuffer={1}x{2} viewports={3} backend={4} backendCapabilities={5} nativeWindowThread={6} renderOwnerThread={7}",
                window.GetHashCode(),
                framebufferSize.X,
                framebufferSize.Y,
                window.Viewports.Count,
                window.WindowBackendKind,
                window.WindowBackendOwnership.Capabilities,
                window.NativeWindowThreadId,
                window.RenderOwnerThreadId);

            Rendering.ApplyRenderPipelinePreference();
            window.SetWorld(windowSettings.TargetWorld is null ? null : XRWorldInstance.GetOrInitWorld(windowSettings.TargetWorld));

            Debug.Rendering(
                "[StartupWindow] Window world assigned hash={0} targetWorld={1} tickLinked={2}",
                window.GetHashCode(),
                window.TargetWorldInstance?.TargetWorldName ?? "<null>",
                window.IsTickLinked);
        }

        private static void WaitForStartupWindowAttachment(TimeSpan timeout)
        {
            if (_windows.Count == 0)
                return;

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                bool allReady = true;
                for (int i = 0; i < _windows.Count; i++)
                {
                    if (_windows[i].IsStartupAttachmentComplete)
                        continue;

                    allReady = false;
                    break;
                }

                if (allReady)
                {
                    Debug.Rendering(
                        "[StartupWindow] Startup attachment barrier satisfied. windows={0} elapsedMs={1:F2}.",
                        _windows.Count,
                        System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    return;
                }

                if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= timeout)
                {
                    Debug.RenderingWarning(
                        "[StartupWindow] Startup attachment barrier timed out. windows={0} elapsedMs={1:F2}. Continuing with diagnostics.",
                        _windows.Count,
                        System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    return;
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Removes a window from the engine and triggers cleanup if it was the last window.
        /// </summary>
        /// <param name="window">The window to remove.</param>
        public static void RemoveWindow(XRWindow window)
        {
            WindowPumpHost.UnregisterWindow(window);
            _windows.Remove(window);
            if (_windows.Count != 0)
                return;

            if (Interlocked.CompareExchange(ref _suppressedCleanupRequests, 0, 0) > 0)
            {
                Interlocked.Decrement(ref _suppressedCleanupRequests);
                return;
            }

            Cleanup();
        }

        /// <summary>
        /// Prevents the engine from shutting down when the final window closes.
        /// </summary>
        /// <remarks>
        /// Useful for temporary utility windows (e.g., file dialogs) that should not
        /// tear down the host application when closed.
        /// </remarks>
        public static void SuppressNextCleanup()
            => Interlocked.Increment(ref _suppressedCleanupRequests);

        /// <summary>
        /// Creates viewports for the specified local players in a window.
        /// </summary>
        private static void CreateViewports(ELocalPlayerIndexMask localPlayerMask, XRWindow window)
        {
            if (localPlayerMask == 0)
                return;

            for (int i = 0; i < 4; i++)
                if (((int)localPlayerMask & (1 << i)) > 0)
                    window.RegisterLocalPlayer((ELocalPlayerIndex)i, false);

            window.ResizeAllViewportsAccordingToPlayers();
        }

        /// <summary>
        /// Resolves the OpenGL context flags. Adds <see cref="ContextFlags.Debug"/>
        /// when the <c>XRE_GL_DEBUG</c> environment variable is set to <c>1</c>, so
        /// that the GL driver delivers low/medium-severity messages through the
        /// existing <c>glDebugMessageCallback</c> handler. Without this flag,
        /// NVIDIA's driver silently filters most diagnostics, which makes
        /// driver-side faults (e.g. <c>FAST_FAIL_CORRUPT_LIST_ENTRY</c>) impossible
        /// to trace from the GL callback.
        /// </summary>
        private static ContextFlags ResolveOpenGLContextFlags()
        {
            var flags = ContextFlags.ForwardCompatible;
            if (XREngine.Rendering.RenderDiagnosticsFlags.GLDebug)
                flags |= ContextFlags.Debug;
            return flags;
        }

        /// <summary>
        /// Builds window options from startup settings.
        /// </summary>
        private static WindowOptions GetWindowOptions(GameWindowStartupSettings windowSettings, bool preferHdrOutput)
            => GetWindowOptions(windowSettings, preferHdrOutput, EInteractiveWindowResizeStrategy.Default);

        private static WindowOptions GetWindowOptions(
            GameWindowStartupSettings windowSettings,
            bool preferHdrOutput,
            EInteractiveWindowResizeStrategy interactiveResizeStrategy)
        {
            WindowState windowState;
            WindowBorder windowBorder;
            Vector2D<int> position = new(windowSettings.X, windowSettings.Y);
            Vector2D<int> size = new(windowSettings.Width, windowSettings.Height);

            switch (windowSettings.WindowState)
            {
                case EWindowState.Fullscreen:
                    windowState = WindowState.Fullscreen;
                    windowBorder = WindowBorder.Hidden;
                    break;
                default:
                case EWindowState.Windowed:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Resizable;
                    break;
                case EWindowState.Borderless:
                    windowState = WindowState.Normal;
                    windowBorder = WindowBorder.Hidden;
                    position = new Vector2D<int>(0, 0);
                    int primaryX = Native.NativeMethods.GetSystemMetrics(0);
                    int primaryY = Native.NativeMethods.GetSystemMetrics(1);
                    size = new Vector2D<int>(primaryX, primaryY);
                    break;
            }

            if (!windowSettings.UseNativeTitleBar && windowState == WindowState.Normal)
                windowBorder = WindowBorder.Hidden;
            if (interactiveResizeStrategy == EInteractiveWindowResizeStrategy.EngineBorderlessResize && windowState == WindowState.Normal)
                windowBorder = WindowBorder.Hidden;

            bool requestHdrSurface = preferHdrOutput && UserSettings.RenderLibrary != ERenderLibrary.Vulkan;
            int preferredBitDepth = requestHdrSurface ? 64 : 24;

            return new(
                true,
                position,
                size,
                0.0,
                0.0,
                UserSettings.RenderLibrary == ERenderLibrary.Vulkan
                    ? new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 1))
                    : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ResolveOpenGLContextFlags(), new APIVersion(4, 6)),
                windowSettings.WindowTitle ?? string.Empty,
                windowState,
                windowBorder,
                ResolveWindowVSyncEnabled(windowSettings.VSync, EffectiveSettings.VSync),
                true,
                VideoMode.Default,
                preferredBitDepth,
                8,
                null,
                windowSettings.TransparentFramebuffer,
                false,
                false,
                null,
                1);
        }

        private static bool ResolveWindowVSyncEnabled(bool windowVSyncRequested, EVSyncMode globalVSyncMode)
            => windowVSyncRequested || globalVSyncMode != EVSyncMode.Off;

        internal static EInteractiveWindowResizeStrategy ResolveInteractiveResizeStrategy(GameWindowStartupSettings? windowSettings = null)
        {
            string envValue = InteractiveWindowResizeStrategyUtility.ResolveEnvironmentValue();
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (InteractiveWindowResizeStrategyUtility.TryParse(envValue, out EInteractiveWindowResizeStrategy envStrategy))
                {
                    Debug.Rendering(
                        "[InteractiveResize] Resolved strategy={0} source=env:{1}.",
                        envStrategy,
                        InteractiveWindowResizeStrategyUtility.EnvironmentVariableName);
                    return envStrategy;
                }

                Debug.RenderingWarning(
                    "[InteractiveResize] Ignoring invalid {0} value '{1}'.",
                    InteractiveWindowResizeStrategyUtility.EnvironmentVariableName,
                    envValue);
            }

            if (windowSettings is not null &&
                windowSettings.InteractiveResizeStrategy != EInteractiveWindowResizeStrategy.Default)
            {
                Debug.Rendering(
                    "[InteractiveResize] Resolved strategy={0} source=window-startup.",
                    windowSettings.InteractiveResizeStrategy);
                return windowSettings.InteractiveResizeStrategy;
            }

            EInteractiveWindowResizeStrategy editorPreference = Engine.EditorPreferences.InteractiveResizeStrategy;
            if (editorPreference != EInteractiveWindowResizeStrategy.Default)
            {
                Debug.Rendering(
                    "[InteractiveResize] Resolved strategy={0} source=editor-preferences.",
                    editorPreference);
                return editorPreference;
            }

            Debug.Rendering("[InteractiveResize] Resolved strategy=Default source=default.");
            return EInteractiveWindowResizeStrategy.Default;
        }

        private static void ApplyInteractiveResizeStrategySettings()
        {
            EInteractiveWindowResizeStrategy strategy = ResolveInteractiveResizeStrategy();
            foreach (var window in _windows)
                window?.SetInteractiveResizeStrategy(strategy);
        }

        private static void ApplyWindowVSyncSettings()
        {
            EVSyncMode vSync = EffectiveSettings.VSync;
            foreach (var window in _windows)
                window?.ApplyVSyncMode(vSync);
        }

        #endregion

        #region Window Focus Handling

        /// <summary>
        /// Handles window focus changes across all engine windows.
        /// </summary>
        private static void WindowFocusChanged(XRWindow window, bool isFocused)
        {
            bool anyWindowFocused = isFocused;
            if (!anyWindowFocused)
            {
                foreach (var w in _windows)
                {
                    if (w == null || w.Window == null)
                        continue; // Skip if the window is null or has been disposed

                    if (w.IsFocused)
                    {
                        anyWindowFocused = true;
                        break;
                    }
                }
            }

            if (LastFocusState == anyWindowFocused)
                return;

            LastFocusState = anyWindowFocused;
            if (anyWindowFocused)
                OnGainedFocus();
            else
                OnLostFocus();
        }

        /// <summary>
        /// Called when all engine windows have lost focus.
        /// Handles audio muting and frame rate throttling.
        /// </summary>
        private static void OnLostFocus()
        {
            Debug.Out("No windows are focused.");

            // Disable audio if configured to do so on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeOut(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = false;
            }

            // Set target FPS to unfocused value (VR headsets manage this independently)
            if (EffectiveSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                float targetRenderFrequency = Time.ResolveRenderFrequency(false, EffectiveSettings.VSync);
                Time.Timer.TargetRenderFrequency = targetRenderFrequency;
                Debug.Out(
                    targetRenderFrequency > 0.0f
                        ? $"Unfocused target FPS set to {targetRenderFrequency}."
                        : "Unfocused render cadence is now display-synchronized by VSync.");
            }

            FocusChanged?.Invoke(false);
        }

        /// <summary>
        /// Called when at least one engine window has gained focus.
        /// Restores audio and normal frame rate.
        /// </summary>
        private static void OnGainedFocus()
        {
            Debug.Out("At least one window is focused.");

            // Re-enable audio if it was disabled on defocus
            if (UserSettings.DisableAudioOnDefocus)
            {
                if (UserSettings.AudioDisableFadeSeconds > 0.0f)
                    Audio.FadeIn(UserSettings.AudioDisableFadeSeconds);
                else
                    Audio.Enabled = true;
            }

            // Restore target FPS to focused value (VR headsets manage this independently)
            if (EffectiveSettings.UnfocusedTargetFramesPerSecond is not null && !VRState.IsInVR)
            {
                float targetRenderFrequency = Time.ResolveRenderFrequency(true, EffectiveSettings.VSync);
                Time.Timer.TargetRenderFrequency = targetRenderFrequency;
                Debug.Out(
                    targetRenderFrequency > 0.0f
                        ? $"Focused target FPS set to {targetRenderFrequency}."
                        : "Focused render cadence is now display-synchronized by VSync.");
            }

            FocusChanged?.Invoke(true);
        }

        #endregion
    }
}
