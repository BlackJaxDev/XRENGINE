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
                    EffectiveSettings.TargetFramesPerSecond ?? 0.0f,
                    EffectiveSettings.TargetUpdatesPerSecond ?? 0.0f,
                    EffectiveSettings.FixedFramesPerSecond,
                    EffectiveSettings.VSync);

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
