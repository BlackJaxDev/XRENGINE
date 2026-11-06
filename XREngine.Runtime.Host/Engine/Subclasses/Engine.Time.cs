using XREngine.Timers;

namespace XREngine
{
    public static partial class Engine
    {
        public static long ElapsedTicks => Time.Timer.TimeTicks();
        public static long StopwatchFrequency => EngineTimer.StopwatchTickFrequency;
        public static long UndilatedDeltaTicks => Time.Timer.Update.DeltaTicks;
        public static long DeltaTicks => ScaleStopwatchTicks(Time.Timer.Update.DeltaTicks, Time.Timer.Update.Dilation);
        public static long FixedDeltaTicks => Time.Timer.FixedUpdateDeltaTicks;
        /// <summary>
        /// This delta is the time that has passed since the last update, in seconds.
        /// Not affected by time dilation.
        /// </summary>
        public static float UndilatedDelta => Time.Timer.Update.Delta;
        /// <summary>
        /// This delta is the time that has passed since the last update, in seconds.
        /// Affected by time dilation.
        /// </summary>
        public static float Delta => Time.Timer.Update.DilatedDelta;
        /// <summary>
        /// This delta is the time that has passed since the last update, in seconds.
        /// Smoothed and not affected by time dilation.
        /// </summary>
        public static float SmoothedUndilatedDelta => Time.Timer.Update.SmoothedDelta;
        /// <summary>
        /// This delta is the time that has passed since the last update, in seconds.
        /// Smoothed and affected by time dilation.
        /// </summary>
        public static float SmoothedDelta => Time.Timer.Update.SmoothedDilatedDelta;
        /// <summary>
        /// This delta is the time that has passed since the last fixed update, in seconds.
        /// Does not vary.
        /// </summary>
        public static float FixedDelta => Time.Timer.FixedUpdateDelta;
        /// <summary>
        /// How many seconds have passed since the game started.
        /// </summary>
        public static float ElapsedTime => Time.Timer.Time();

        public static class Time
        {
            public static EngineTimer Timer { get; } = new EngineTimer();
            public static long ElapsedTicks => Timer.TimeTicks();
            public static long StopwatchFrequency => EngineTimer.StopwatchTickFrequency;
            public static long UndilatedDeltaTicks => Timer.Update.DeltaTicks;
            public static long DeltaTicks => ScaleStopwatchTicks(Timer.Update.DeltaTicks, Timer.Update.Dilation);
            public static long FixedDeltaTicks => Timer.FixedUpdateDeltaTicks;

            static Time() => Timer = new EngineTimer();

            private static readonly List<DateTime> _debugTimers = [];
            /// <summary>
            /// Starts a quick timer to track the number of sceonds elapsed.
            /// Returns the id of the timer.
            /// </summary>
            public static int StartDebugTimer()
            {
                int id = _debugTimers.Count;
                _debugTimers.Add(DateTime.Now);
                return id;
            }
            /// <summary>
            /// Ends the timer and returns the amount of time elapsed, in seconds.
            /// </summary>
            /// <param name="id">The id of the timer.</param>
            public static float EndDebugTimer(int id)
            {
                float seconds = (float)(DateTime.Now - _debugTimers[id]).TotalSeconds;
                _debugTimers.RemoveAt(id);
                return seconds;
            }

            /// <summary>
            /// Given the game's and user's settings, updates the core game engine timer settings.
            /// </summary>
            /// <param name="gameSet"></param>
            /// <param name="userSet"></param>
            public static void Initialize(GameStartupSettings gameSet, UserSettings userSet)
                => UpdateTimer(
                    ResolveRenderFrequency(LastFocusState, EffectiveSettings.VSync),
                    EffectiveSettings.TargetUpdatesPerSecond ?? 0.0f,
                    EffectiveSettings.FixedFramesPerSecond,
                    EffectiveSettings.VSync);

            /// <summary>
            /// Resolves the engine render limit, in hertz. Zero removes the engine limit.
            /// </summary>
            /// <remarks>
            /// While a VR runtime owns presentation, the VR limit replaces the desktop, unfocused, and VSync rules.
            /// The VR runtime paces each frame to the headset display, so an engine limit there only adds a second cap.
            /// </remarks>
            public static float ResolveRenderFrequency(bool focused, EVSyncMode vSync)
            {
                if (RuntimeEngine.VRState.IsInVR)
                    return ResolveVrRenderFrequency();

                float? focusedTarget = EffectiveSettings.TargetFramesPerSecond;
                if (!focused)
                {
                    float? unfocusedTarget = EffectiveSettings.UnfocusedTargetFramesPerSecond;
                    if (HasExplicitUnfocusedRenderLimit(focusedTarget, unfocusedTarget))
                        return unfocusedTarget ?? 0.0f;

                    // A background editor must not take GPU time from the app that has focus.
                    if (unfocusedTarget is null && !IsDesktopRenderUncapped())
                    {
                        float focusedLimit = ResolveConfiguredRenderFrequency(focusedTarget, vSync);
                        return focusedLimit >= 1.0f
                            ? Math.Min(focusedLimit, DefaultUnfocusedRenderFrequency)
                            : DefaultUnfocusedRenderFrequency;
                    }
                }

                return ResolveConfiguredRenderFrequency(focusedTarget, vSync);
            }

            /// <summary>
            /// Render limit, in hertz, while no engine window has focus and
            /// <see cref="EffectiveSettings.UnfocusedTargetFramesPerSecond"/> is null.
            /// <see cref="XREngineEnvironmentVariables.DesktopRenderUncapped"/> removes this default.
            /// </summary>
            public const float DefaultUnfocusedRenderFrequency = 30.0f;

            /// <summary>
            /// Resolves the engine render limit for VR, in hertz. Null and values below 1 remove the engine limit.
            /// </summary>
            public static float ResolveVrRenderFrequency()
                => EffectiveSettings.VrTargetFramesPerSecond is float target && target >= 1.0f
                    ? target
                    : 0.0f;

            /// <summary>
            /// Resolves the desktop render limit. VSync paces presentation itself, so it needs no engine limit.
            /// </summary>
            /// <remarks>
            /// With VSync off and no target (null or below 1), the limit is the primary display refresh rate.
            /// An uncapped desktop keeps the GPU at 100% all the time, even at a low frame rate. The Windows
            /// compositor must then wait for the GPU, and the whole PC becomes slow. Set
            /// <see cref="XREngineEnvironmentVariables.DesktopRenderUncapped"/> to 1 to remove the limit for benchmarks.
            /// </remarks>
            private static float ResolveConfiguredRenderFrequency(float? configuredTargetRenderFrequency, EVSyncMode vSync)
            {
                if (vSync != EVSyncMode.Off)
                    return 0.0f;

                if (configuredTargetRenderFrequency is float target && target >= 1.0f)
                    return target;

                return IsDesktopRenderUncapped()
                    ? 0.0f
                    : DesktopDisplayRefreshRate.Get();
            }

            private static bool IsDesktopRenderUncapped()
            {
                string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.DesktopRenderUncapped);
                return value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            }

            private static bool HasExplicitUnfocusedRenderLimit(float? focusedTarget, float? unfocusedTarget)
                => unfocusedTarget is float unfocused
                    && (focusedTarget is not float focused || Math.Abs(unfocused - focused) > 0.0001f);

            /// <summary>
            /// Updates the core game engine timer settings.
            /// </summary>
            /// <param name="singleThreaded"></param>
            /// <param name="targetRenderFrequency"></param>
            /// <param name="targetUpdateFrequency"></param>
            public static void UpdateTimer(
                float targetRenderFrequency,
                float targetUpdateFrequency,
                float fixedUpdateFrequency,
                EVSyncMode vSync)
            {
                Timer.TargetRenderFrequency = targetRenderFrequency;
                Timer.TargetUpdateFrequency = targetUpdateFrequency;
                Timer.FixedUpdateFrequency = fixedUpdateFrequency;
                Timer.VSync = vSync;
            }
        }

        private static long ScaleStopwatchTicks(long deltaTicks, float speed)
            => deltaTicks == 0L || !float.IsFinite(speed) || speed == 0.0f
                ? 0L
                : (long)Math.Round(deltaTicks * (double)speed);
    }
}
