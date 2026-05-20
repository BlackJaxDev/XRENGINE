using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                private static int _gpuCpuFallbackEvents;
                private static int _gpuCpuFallbackRecoveredCommands;
                private static int _forbiddenGpuFallbackEvents;
                private static int _lastFrameGpuCpuFallbackEvents;
                private static int _lastFrameGpuCpuFallbackRecoveredCommands;
                private static int _lastFrameForbiddenGpuFallbackEvents;

                /// <summary>
                /// Number of GPU->CPU culling fallback events in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackEvents => _lastFrameGpuCpuFallbackEvents;

                /// <summary>
                /// Number of commands recovered by GPU->CPU fallback in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackRecoveredCommands => _lastFrameGpuCpuFallbackRecoveredCommands;

                /// <summary>
                /// Number of forbidden fallback attempts observed in the last completed frame.
                /// Forbidden fallbacks indicate shipping-profile behavior would have fallen back but was blocked.
                /// </summary>
                public static int ForbiddenGpuFallbackEvents => _lastFrameForbiddenGpuFallbackEvents;

                /// <summary>
                /// Records usage of GPU->CPU fallback recovery during culling.
                /// </summary>
                public static void RecordGpuCpuFallback(int eventCount, int recoveredCommands)
                {
                    if (!EnableTracking || eventCount <= 0)
                        return;

                    Interlocked.Add(ref _gpuCpuFallbackEvents, eventCount);
                    if (recoveredCommands > 0)
                        Interlocked.Add(ref _gpuCpuFallbackRecoveredCommands, recoveredCommands);
                }

                /// <summary>
                /// Records a forbidden fallback attempt (fallback blocked by profile policy).
                /// </summary>
                public static void RecordForbiddenGpuFallback(int eventCount = 1)
                {
                    if (!EnableTracking || eventCount <= 0)
                        return;

                    Interlocked.Add(ref _forbiddenGpuFallbackEvents, eventCount);
                }
            }
        }
    }
}
