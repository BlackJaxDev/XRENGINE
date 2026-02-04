using Silk.NET.Maths;
using Silk.NET.Windowing;
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
            foreach (var windowSettings in windows)
                CreateWindow(windowSettings);
        }

        /// <summary>
        /// Creates a new engine window with the specified settings.
        /// </summary>
        /// <param name="windowSettings">Configuration for the window.</param>
        /// <returns>The created window instance.</returns>
        /// <remarks>
        /// If Vulkan initialization fails, automatically falls back to OpenGL.
        /// </remarks>
        public static XRWindow CreateWindow(GameWindowStartupSettings windowSettings)
        {
            bool preferHdrOutput = windowSettings.OutputHDR ?? Rendering.Settings.OutputHDR;
            var options = GetWindowOptions(windowSettings, preferHdrOutput);

            XRWindow window;
            try
            {
                window = new XRWindow(options, windowSettings.UseNativeTitleBar);
            }
            catch (Exception ex) when (options.API.API == ContextAPI.Vulkan)
            {
                Debug.RenderingWarning($"Vulkan initialization failed, falling back to OpenGL: {ex.Message}");
                options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
                window = new XRWindow(options, windowSettings.UseNativeTitleBar);
            }

            window.PreferHDROutput = preferHdrOutput;
            CreateViewports(windowSettings.LocalPlayers, window);
            window.UpdateViewportSizes();
            _windows.Add(window);

            Rendering.ApplyRenderPipelinePreference();
            window.SetWorld(windowSettings.TargetWorld);

            return window;
        }

        /// <summary>
        /// Removes a window from the engine and triggers cleanup if it was the last window.
        /// </summary>
        /// <param name="window">The window to remove.</param>
        public static void RemoveWindow(XRWindow window)
        {
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
        /// Builds window options from startup settings.
        /// </summary>
        private static WindowOptions GetWindowOptions(GameWindowStartupSettings windowSettings, bool preferHdrOutput)
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
                    : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)),
                windowSettings.WindowTitle ?? string.Empty,
                windowState,
                windowBorder,
                windowSettings.VSync,
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
                Time.Timer.TargetRenderFrequency = EffectiveSettings.UnfocusedTargetFramesPerSecond.Value;
                Debug.Out($"Unfocused target FPS set to {EffectiveSettings.UnfocusedTargetFramesPerSecond}.");
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
                Time.Timer.TargetRenderFrequency = EffectiveSettings.TargetFramesPerSecond ?? 0.0f;
                Debug.Out($"Focused target FPS set to {EffectiveSettings.TargetFramesPerSecond}.");
            }

            FocusChanged?.Invoke(true);
        }

        #endregion
    }
}
