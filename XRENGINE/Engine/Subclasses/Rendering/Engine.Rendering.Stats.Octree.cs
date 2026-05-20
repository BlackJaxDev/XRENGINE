using System.Threading;
using XREngine.Data.Trees;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                // Octree stats
                private static readonly object _octreeTimingStatsLock = new();
                private static int _octreeCollectCallsCurrent;
                private static int _octreeVisibleRenderablesCurrent;
                private static int _octreeEmittedCommandsCurrent;
                private static int _octreeMaxVisibleRenderablesCurrent;
                private static int _octreeMaxEmittedCommandsCurrent;
                private static int _octreeAddCommandsCurrent;
                private static int _octreeMoveCommandsCurrent;
                private static int _octreeRemoveCommandsCurrent;
                private static int _octreeSkippedMovesCurrent;
                private static int _octreeSwapDrainedCommandsCurrent;
                private static int _octreeSwapBufferedCommandsCurrent;
                private static int _octreeSwapExecutedCommandsCurrent;
                private static long _octreeSwapDrainTicksCurrent;
                private static long _octreeSwapExecuteTicksCurrent;
                private static long _octreeSwapMaxCommandTicksCurrent;
                private static int _octreeSwapMaxCommandKindCurrent;
                private static int _octreeRaycastProcessedCommandsCurrent;
                private static int _octreeRaycastDroppedCommandsCurrent;
                private static long _octreeRaycastTraversalTicksCurrent;
                private static long _octreeRaycastCallbackTicksCurrent;
                private static long _octreeRaycastMaxTraversalTicksCurrent;
                private static long _octreeRaycastMaxCallbackTicksCurrent;
                private static long _octreeRaycastMaxCommandTicksCurrent;
                private static int _octreeCollectCallsDisplay;
                private static int _octreeVisibleRenderablesDisplay;
                private static int _octreeEmittedCommandsDisplay;
                private static int _octreeMaxVisibleRenderablesDisplay;
                private static int _octreeMaxEmittedCommandsDisplay;
                private static int _octreeAddCommandsDisplay;
                private static int _octreeMoveCommandsDisplay;
                private static int _octreeRemoveCommandsDisplay;
                private static int _octreeSkippedMovesDisplay;
                private static int _octreeSwapDrainedCommandsDisplay;
                private static int _octreeSwapBufferedCommandsDisplay;
                private static int _octreeSwapExecutedCommandsDisplay;
                private static long _octreeSwapDrainTicksDisplay;
                private static long _octreeSwapExecuteTicksDisplay;
                private static long _octreeSwapMaxCommandTicksDisplay;
                private static int _octreeSwapMaxCommandKindDisplay;
                private static int _octreeRaycastProcessedCommandsDisplay;
                private static int _octreeRaycastDroppedCommandsDisplay;
                private static long _octreeRaycastTraversalTicksDisplay;
                private static long _octreeRaycastCallbackTicksDisplay;
                private static long _octreeRaycastMaxTraversalTicksDisplay;
                private static long _octreeRaycastMaxCallbackTicksDisplay;
                private static long _octreeRaycastMaxCommandTicksDisplay;
                private static int _octreeStatsDirty;
                private static bool _octreeStatsReady;

                public static bool EnableOctreeStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif
                public static bool OctreeStatsReady => _octreeStatsReady;
                public static int OctreeCollectCallCount => _octreeCollectCallsDisplay;
                public static int OctreeVisibleRenderableCount => _octreeVisibleRenderablesDisplay;
                public static int OctreeEmittedCommandCount => _octreeEmittedCommandsDisplay;
                public static int OctreeMaxVisibleRenderablesPerCollect => _octreeMaxVisibleRenderablesDisplay;
                public static int OctreeMaxEmittedCommandsPerCollect => _octreeMaxEmittedCommandsDisplay;
                public static int OctreeAddCount => _octreeAddCommandsDisplay;
                public static int OctreeMoveCount => _octreeMoveCommandsDisplay;
                public static int OctreeRemoveCount => _octreeRemoveCommandsDisplay;
                public static int OctreeSkippedMoveCount => _octreeSkippedMovesDisplay;
                public static int OctreeSwapDrainedCommandCount => _octreeSwapDrainedCommandsDisplay;
                public static int OctreeSwapBufferedCommandCount => _octreeSwapBufferedCommandsDisplay;
                public static int OctreeSwapExecutedCommandCount => _octreeSwapExecutedCommandsDisplay;
                public static double OctreeSwapDrainMs => StopwatchTicksToMilliseconds(_octreeSwapDrainTicksDisplay);
                public static double OctreeSwapExecuteMs => StopwatchTicksToMilliseconds(_octreeSwapExecuteTicksDisplay);
                public static double OctreeSwapMaxCommandMs => StopwatchTicksToMilliseconds(_octreeSwapMaxCommandTicksDisplay);
                public static string OctreeSwapMaxCommandKind => GetOctreeCommandKindName(_octreeSwapMaxCommandKindDisplay);
                public static int OctreeRaycastProcessedCommandCount => _octreeRaycastProcessedCommandsDisplay;
                public static int OctreeRaycastDroppedCommandCount => _octreeRaycastDroppedCommandsDisplay;
                public static double OctreeRaycastTraversalMs => StopwatchTicksToMilliseconds(_octreeRaycastTraversalTicksDisplay);
                public static double OctreeRaycastCallbackMs => StopwatchTicksToMilliseconds(_octreeRaycastCallbackTicksDisplay);
                public static double OctreeRaycastMaxTraversalMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxTraversalTicksDisplay);
                public static double OctreeRaycastMaxCallbackMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxCallbackTicksDisplay);
                public static double OctreeRaycastMaxCommandMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxCommandTicksDisplay);

                public static void RecordOctreeAdd()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeAddCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeMoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeRemove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeRemoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeSkippedMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeSkippedMovesCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeCollect(int visibleRenderables, int emittedCommands)
                {
                    if (!EnableOctreeStats)
                        return;

                    Interlocked.Increment(ref _octreeCollectCallsCurrent);
                    if (visibleRenderables > 0)
                    {
                        Interlocked.Add(ref _octreeVisibleRenderablesCurrent, visibleRenderables);
                        UpdateMaxCounter(ref _octreeMaxVisibleRenderablesCurrent, visibleRenderables);
                    }

                    if (emittedCommands > 0)
                    {
                        Interlocked.Add(ref _octreeEmittedCommandsCurrent, emittedCommands);
                        UpdateMaxCounter(ref _octreeMaxEmittedCommandsCurrent, emittedCommands);
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeSwapTiming(OctreeSwapTimingStats stats)
                {
                    if (!EnableOctreeStats)
                        return;

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeSwapDrainedCommandsCurrent += stats.DrainedCommandCount;
                        _octreeSwapBufferedCommandsCurrent += stats.BufferedCommandCount;
                        _octreeSwapExecutedCommandsCurrent += stats.ExecutedCommandCount;
                        _octreeSwapDrainTicksCurrent += stats.DrainTicks;
                        _octreeSwapExecuteTicksCurrent += stats.ExecuteTicks;

                        if (stats.MaxCommandTicks > _octreeSwapMaxCommandTicksCurrent)
                        {
                            _octreeSwapMaxCommandTicksCurrent = stats.MaxCommandTicks;
                            _octreeSwapMaxCommandKindCurrent = (int)stats.MaxCommandKind;
                        }
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeRaycastTiming(OctreeRaycastTimingStats stats)
                {
                    if (!EnableOctreeStats)
                        return;

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeRaycastProcessedCommandsCurrent += stats.ProcessedCommandCount;
                        _octreeRaycastDroppedCommandsCurrent += stats.DroppedCommandCount;
                        _octreeRaycastTraversalTicksCurrent += stats.TraversalTicks;
                        _octreeRaycastCallbackTicksCurrent += stats.CallbackTicks;

                        if (stats.MaxTraversalTicks > _octreeRaycastMaxTraversalTicksCurrent)
                            _octreeRaycastMaxTraversalTicksCurrent = stats.MaxTraversalTicks;

                        if (stats.MaxCallbackTicks > _octreeRaycastMaxCallbackTicksCurrent)
                            _octreeRaycastMaxCallbackTicksCurrent = stats.MaxCallbackTicks;

                        if (stats.MaxCommandTicks > _octreeRaycastMaxCommandTicksCurrent)
                            _octreeRaycastMaxCommandTicksCurrent = stats.MaxCommandTicks;
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void SwapOctreeStats()
                {
                    if (!EnableOctreeStats) return;
                    if (Interlocked.Exchange(ref _octreeStatsDirty, 0) == 0) return;

                    _octreeCollectCallsDisplay = Interlocked.Exchange(ref _octreeCollectCallsCurrent, 0);
                    _octreeVisibleRenderablesDisplay = Interlocked.Exchange(ref _octreeVisibleRenderablesCurrent, 0);
                    _octreeEmittedCommandsDisplay = Interlocked.Exchange(ref _octreeEmittedCommandsCurrent, 0);
                    _octreeMaxVisibleRenderablesDisplay = Interlocked.Exchange(ref _octreeMaxVisibleRenderablesCurrent, 0);
                    _octreeMaxEmittedCommandsDisplay = Interlocked.Exchange(ref _octreeMaxEmittedCommandsCurrent, 0);
                    _octreeAddCommandsDisplay = Interlocked.Exchange(ref _octreeAddCommandsCurrent, 0);
                    _octreeMoveCommandsDisplay = Interlocked.Exchange(ref _octreeMoveCommandsCurrent, 0);
                    _octreeRemoveCommandsDisplay = Interlocked.Exchange(ref _octreeRemoveCommandsCurrent, 0);
                    _octreeSkippedMovesDisplay = Interlocked.Exchange(ref _octreeSkippedMovesCurrent, 0);

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeSwapDrainedCommandsDisplay = _octreeSwapDrainedCommandsCurrent;
                        _octreeSwapBufferedCommandsDisplay = _octreeSwapBufferedCommandsCurrent;
                        _octreeSwapExecutedCommandsDisplay = _octreeSwapExecutedCommandsCurrent;
                        _octreeSwapDrainTicksDisplay = _octreeSwapDrainTicksCurrent;
                        _octreeSwapExecuteTicksDisplay = _octreeSwapExecuteTicksCurrent;
                        _octreeSwapMaxCommandTicksDisplay = _octreeSwapMaxCommandTicksCurrent;
                        _octreeSwapMaxCommandKindDisplay = _octreeSwapMaxCommandKindCurrent;
                        _octreeRaycastProcessedCommandsDisplay = _octreeRaycastProcessedCommandsCurrent;
                        _octreeRaycastDroppedCommandsDisplay = _octreeRaycastDroppedCommandsCurrent;
                        _octreeRaycastTraversalTicksDisplay = _octreeRaycastTraversalTicksCurrent;
                        _octreeRaycastCallbackTicksDisplay = _octreeRaycastCallbackTicksCurrent;
                        _octreeRaycastMaxTraversalTicksDisplay = _octreeRaycastMaxTraversalTicksCurrent;
                        _octreeRaycastMaxCallbackTicksDisplay = _octreeRaycastMaxCallbackTicksCurrent;
                        _octreeRaycastMaxCommandTicksDisplay = _octreeRaycastMaxCommandTicksCurrent;

                        _octreeSwapDrainedCommandsCurrent = 0;
                        _octreeSwapBufferedCommandsCurrent = 0;
                        _octreeSwapExecutedCommandsCurrent = 0;
                        _octreeSwapDrainTicksCurrent = 0L;
                        _octreeSwapExecuteTicksCurrent = 0L;
                        _octreeSwapMaxCommandTicksCurrent = 0L;
                        _octreeSwapMaxCommandKindCurrent = 0;
                        _octreeRaycastProcessedCommandsCurrent = 0;
                        _octreeRaycastDroppedCommandsCurrent = 0;
                        _octreeRaycastTraversalTicksCurrent = 0L;
                        _octreeRaycastCallbackTicksCurrent = 0L;
                        _octreeRaycastMaxTraversalTicksCurrent = 0L;
                        _octreeRaycastMaxCallbackTicksCurrent = 0L;
                        _octreeRaycastMaxCommandTicksCurrent = 0L;
                    }

                    _octreeStatsReady = true;
                }

                private static string GetOctreeCommandKindName(int kind)
                    => kind switch
                    {
                        (int)EOctreeCommandKind.Add => nameof(EOctreeCommandKind.Add),
                        (int)EOctreeCommandKind.Move => nameof(EOctreeCommandKind.Move),
                        (int)EOctreeCommandKind.Remove => nameof(EOctreeCommandKind.Remove),
                        _ => nameof(EOctreeCommandKind.None),
                    };
            }
        }
    }
}
