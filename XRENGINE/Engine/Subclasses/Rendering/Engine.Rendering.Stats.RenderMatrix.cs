using System;
using System.Collections.Generic;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class RenderMatrix
                {
                    // Render-matrix stats use a separate swap cycle aligned with SwapBuffers phase.
                    // Current = being written now, Display = last completed swap, Ready = waiting to become Display.
                    private static int _renderMatrixAppliedCurrent;
                    private static int _renderMatrixBatchCountCurrent;
                    private static int _renderMatrixMaxBatchSizeCurrent;
                    private static int _renderMatrixSetCallsCurrent;
                    private static int _renderMatrixListenerInvocationsCurrent;
                    private static int _renderMatrixAppliedDisplay;
                    private static int _renderMatrixBatchCountDisplay;
                    private static int _renderMatrixMaxBatchSizeDisplay;
                    private static int _renderMatrixSetCallsDisplay;
                    private static int _renderMatrixListenerInvocationsDisplay;
                    private static readonly object _renderMatrixStatsLock = new();
                    private static Dictionary<string, int> _renderMatrixListenerCountsCurrent = new(StringComparer.Ordinal);
                    private static Dictionary<string, int> _renderMatrixListenerCountsDisplay = new(StringComparer.Ordinal);
                    private static bool _renderMatrixStatsReady;
                    private static int _renderMatrixStatsDirty;

                    /// <summary>
                    /// Enables collection of render-matrix statistics.
                    /// </summary>
                    public static bool EnableRenderMatrixStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                        true;
#endif

                    /// <summary>
                    /// Enables detailed render-matrix listener tracking (per listener type).
                    /// </summary>
                    public static bool EnableRenderMatrixListenerTracking { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                        true;
#endif

                    /// <summary>
                    /// Whether render-matrix stats have been populated at least once.
                    /// </summary>
                    public static bool RenderMatrixStatsReady => _renderMatrixStatsReady;

                    /// <summary>
                    /// Number of render-matrix updates applied in the last completed frame.
                    /// </summary>
                    public static int RenderMatrixApplied => _renderMatrixAppliedDisplay;

                    /// <summary>
                    /// Number of non-empty render-matrix batches applied in the last completed frame.
                    /// </summary>
                    public static int RenderMatrixBatchCount => _renderMatrixBatchCountDisplay;

                    /// <summary>
                    /// Largest render-matrix batch applied in the last completed frame.
                    /// </summary>
                    public static int RenderMatrixMaxBatchSize => _renderMatrixMaxBatchSizeDisplay;

                    /// <summary>
                    /// Number of SetRenderMatrix calls in the last completed frame.
                    /// </summary>
                    public static int RenderMatrixSetCalls => _renderMatrixSetCallsDisplay;

                    /// <summary>
                    /// Total number of render-matrix listener invocations in the last completed frame.
                    /// </summary>
                    public static int RenderMatrixListenerInvocations => _renderMatrixListenerInvocationsDisplay;

                    /// <summary>
                    /// Swaps render-matrix stats from current to display buffer. Call from SwapBuffers phase.
                    /// </summary>
                    public static void SwapRenderMatrixStats()
                    {
                        if (!EnableRenderMatrixStats)
                            return;

                        if (Interlocked.Exchange(ref _renderMatrixStatsDirty, 0) == 0)
                            return;

                        // Atomically copy current values to display and reset current.
                        _renderMatrixAppliedDisplay = Interlocked.Exchange(ref _renderMatrixAppliedCurrent, 0);
                        _renderMatrixBatchCountDisplay = Interlocked.Exchange(ref _renderMatrixBatchCountCurrent, 0);
                        _renderMatrixMaxBatchSizeDisplay = Interlocked.Exchange(ref _renderMatrixMaxBatchSizeCurrent, 0);
                        _renderMatrixSetCallsDisplay = Interlocked.Exchange(ref _renderMatrixSetCallsCurrent, 0);
                        _renderMatrixListenerInvocationsDisplay = Interlocked.Exchange(ref _renderMatrixListenerInvocationsCurrent, 0);

                        lock (_renderMatrixStatsLock)
                        {
                            (_renderMatrixListenerCountsCurrent, _renderMatrixListenerCountsDisplay) = (_renderMatrixListenerCountsDisplay, _renderMatrixListenerCountsCurrent);
                            _renderMatrixListenerCountsCurrent.Clear();
                        }

                        _renderMatrixStatsReady = true;
                    }

                    /// <summary>
                    /// Record the number of render-matrix updates applied during swap buffers.
                    /// </summary>
                    public static void RecordRenderMatrixApplied(int count)
                    {
                        if (!EnableRenderMatrixStats || count <= 0)
                            return;

                        Interlocked.Add(ref _renderMatrixAppliedCurrent, count);
                        Interlocked.Increment(ref _renderMatrixBatchCountCurrent);
                        UpdateMaxCounter(ref _renderMatrixMaxBatchSizeCurrent, count);
                        Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);
                    }

                    /// <summary>
                    /// Record a render-matrix change event and (optionally) its listeners.
                    /// </summary>
                    public static void RecordRenderMatrixChange(Delegate? listeners)
                    {
                        if (!EnableRenderMatrixStats)
                            return;

                        Interlocked.Increment(ref _renderMatrixSetCallsCurrent);
                        Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);

                        if (!EnableRenderMatrixListenerTracking || listeners is null)
                            return;

                        var invocationList = listeners.GetInvocationList();
                        Interlocked.Add(ref _renderMatrixListenerInvocationsCurrent, invocationList.Length);

                        lock (_renderMatrixStatsLock)
                        {
                            foreach (var handler in invocationList)
                            {
                                var key = handler.Target?.GetType().Name ?? handler.Method.DeclaringType?.Name ?? "Static";
                                if (_renderMatrixListenerCountsCurrent.TryGetValue(key, out int current))
                                    _renderMatrixListenerCountsCurrent[key] = current + 1;
                                else
                                    _renderMatrixListenerCountsCurrent[key] = 1;
                            }
                        }
                    }

                    /// <summary>
                    /// Returns the last-frame snapshot of render-matrix listener counts per listener type.
                    /// </summary>
                    public static KeyValuePair<string, int>[] GetRenderMatrixListenerSnapshot()
                    {
                        lock (_renderMatrixStatsLock)
                        {
                            if (_renderMatrixListenerCountsDisplay.Count == 0)
                                return [];

                            var copy = new KeyValuePair<string, int>[_renderMatrixListenerCountsDisplay.Count];
                            int index = 0;
                            foreach (var pair in _renderMatrixListenerCountsDisplay)
                                copy[index++] = pair;
                            return copy;
                        }
                    }
                }
            }
        }
    }
}
