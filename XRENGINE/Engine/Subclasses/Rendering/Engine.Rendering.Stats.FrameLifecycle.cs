using System;
using System.Threading;
using XREngine.Timers;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public enum EFrameLifecycleWaitReason
                {
                    None = 0,
                    WaitingForRenderThread = 1,
                    WaitingForCollectVisible = 2,
                    ReusingPreviousVisibility = 3,
                }

                /// <summary>
                /// Cross-thread frame lifecycle telemetry for the update/collect/swap/render fence chain.
                /// </summary>
                public static class FrameLifecycle
                {
                    private static long _collectWaitForRenderTicks;
                    private static long _renderWaitForCollectTicks;
                    private static int _collectWaitReason;
                    private static int _renderWaitReason;
                    private static int _skippedCollectFrames;
                    private static int _staleCollectReuseFrames;

                    private static long _lastFrameCollectWaitForRenderTicks;
                    private static long _lastFrameRenderWaitForCollectTicks;
                    private static int _lastFrameCollectWaitReason;
                    private static int _lastFrameRenderWaitReason;
                    private static int _lastFrameSkippedCollectFrames;
                    private static int _lastFrameStaleCollectReuseFrames;

                    public static double CollectWaitForRenderMs
                        => StopwatchTicksToMilliseconds(_lastFrameCollectWaitForRenderTicks);

                    public static double RenderWaitForCollectMs
                        => StopwatchTicksToMilliseconds(_lastFrameRenderWaitForCollectTicks);

                    public static string CollectWaitReason
                        => ((EFrameLifecycleWaitReason)_lastFrameCollectWaitReason).ToString();

                    public static string RenderWaitReason
                        => ((EFrameLifecycleWaitReason)_lastFrameRenderWaitReason).ToString();

                    public static int SkippedCollectFrames => _lastFrameSkippedCollectFrames;
                    public static int StaleCollectReuseFrames => _lastFrameStaleCollectReuseFrames;
                    public static string CollectVisibleLatePolicy => Engine.Time.Timer.CollectVisibleLatePolicy.ToString();
                    public static ulong UpdateFrameId => Engine.Time.Timer.UpdateFrameId;
                    public static ulong CollectFrameId => Engine.Time.Timer.CollectFrameId;
                    public static ulong SwapFrameId => Engine.Time.Timer.SwapFrameId;
                    public static ulong RenderFrameId => Engine.Rendering.State.RenderFrameId;
                    public static ulong PresentFrameId => Engine.Time.Timer.PresentFrameId;

                    internal static void RecordCollectWaitForRender(long stopwatchTicks)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _collectWaitForRenderTicks, Math.Max(0L, stopwatchTicks));
                        if (stopwatchTicks > 0L)
                            Interlocked.Exchange(ref _collectWaitReason, (int)EFrameLifecycleWaitReason.WaitingForRenderThread);
                    }

                    internal static void RecordRenderWaitForCollect(long stopwatchTicks)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _renderWaitForCollectTicks, Math.Max(0L, stopwatchTicks));
                        if (stopwatchTicks > 0L)
                            Interlocked.Exchange(ref _renderWaitReason, (int)EFrameLifecycleWaitReason.WaitingForCollectVisible);
                    }

                    internal static void RecordStaleCollectReuse()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _staleCollectReuseFrames);
                        Interlocked.Increment(ref _skippedCollectFrames);
                        Interlocked.Exchange(ref _renderWaitReason, (int)EFrameLifecycleWaitReason.ReusingPreviousVisibility);
                    }

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameCollectWaitForRenderTicks = Interlocked.Exchange(ref _collectWaitForRenderTicks, 0);
                        _lastFrameRenderWaitForCollectTicks = Interlocked.Exchange(ref _renderWaitForCollectTicks, 0);
                        _lastFrameCollectWaitReason = Interlocked.Exchange(ref _collectWaitReason, 0);
                        _lastFrameRenderWaitReason = Interlocked.Exchange(ref _renderWaitReason, 0);
                        _lastFrameSkippedCollectFrames = Interlocked.Exchange(ref _skippedCollectFrames, 0);
                        _lastFrameStaleCollectReuseFrames = Interlocked.Exchange(ref _staleCollectReuseFrames, 0);
                    }

                    private static double StopwatchTicksToMilliseconds(long ticks)
                        => ticks <= 0L ? 0.0 : ticks * 1000.0 / EngineTimer.StopwatchTickFrequency;
                }
            }
        }
    }
}
