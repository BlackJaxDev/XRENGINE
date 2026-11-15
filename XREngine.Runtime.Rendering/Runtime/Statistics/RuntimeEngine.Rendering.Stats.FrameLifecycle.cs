using System;
using System.Threading;
using XREngine.Rendering;
using XREngine.Timers;

namespace XREngine
{
    public static partial class RuntimeEngine
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
                public static partial class FrameLifecycle
                {
                    private static long _collectWaitForRenderTicks;
                    private static long _renderWaitForCollectTicks;
                    private static long _renderWaitForXrFrameTicks;
                    private static int _collectWaitReason;
                    private static int _renderWaitReason;
                    private static int _skippedCollectFrames;
                    private static int _staleCollectReuseFrames;
                    private static long _framePackageProductionTicks;
                    private static long _framePackagePublicationTicks;
                    private static long _framePackageValidationTicks;
                    private static long _framePackageConsumptionTicks;
                    private static int _framePackagesPrepared;
                    private static int _framePackagesPublished;
                    private static int _framePackagesConsumed;
                    private static int _framePackagesPreparedLate;
                    private static int _framePackagesRejected;
                    private static int _framePackageGenerationAge;

                    private static long _lastFrameCollectWaitForRenderTicks;
                    private static long _lastFrameRenderWaitForCollectTicks;
                    private static long _lastFrameRenderWaitForXrFrameTicks;
                    private static int _lastFrameCollectWaitReason;
                    private static int _lastFrameRenderWaitReason;
                    private static int _lastFrameSkippedCollectFrames;
                    private static int _lastFrameStaleCollectReuseFrames;
                    private static long _lastFramePackageProductionTicks;
                    private static long _lastFramePackagePublicationTicks;
                    private static long _lastFramePackageValidationTicks;
                    private static long _lastFramePackageConsumptionTicks;
                    private static int _lastFramePackagesPrepared;
                    private static int _lastFramePackagesPublished;
                    private static int _lastFramePackagesConsumed;
                    private static int _lastFramePackagesPreparedLate;
                    private static int _lastFramePackagesRejected;
                    private static int _lastFramePackageGenerationAge;

                    public static double CollectWaitForRenderMs
                        => StopwatchTicksToMilliseconds(_lastFrameCollectWaitForRenderTicks);

                    /// <summary>
                    /// Render-thread wait for a publication while the collect thread collected or
                    /// swapped, in the last sampled frame. Excludes <see cref="RenderWaitForXrFrameMs"/>.
                    /// </summary>
                    public static double RenderWaitForCollectMs
                        => StopwatchTicksToMilliseconds(_lastFrameRenderWaitForCollectTicks);

                    /// <summary>
                    /// Render-thread wait for a publication that the collect thread held for
                    /// <c>xrWaitFrame</c> (XR collect pacing), in the last sampled frame.
                    /// </summary>
                    public static double RenderWaitForXrFrameMs
                        => StopwatchTicksToMilliseconds(_lastFrameRenderWaitForXrFrameTicks);

                    public static string CollectWaitReason
                        => ((EFrameLifecycleWaitReason)_lastFrameCollectWaitReason).ToString();

                    public static string RenderWaitReason
                        => ((EFrameLifecycleWaitReason)_lastFrameRenderWaitReason).ToString();

                    private static long _renderCircuitBreakerTrips;
                    private static long _desktopSecondHalfBusyStops;

                    /// <summary>
                    /// Total count of split desktop frames (VR desktop WorkerRecord mode) whose
                    /// second half stopped because a swapchain image was still in use on the GPU.
                    /// The desktop window keeps its last image for that opportunity, and the next
                    /// opportunity resumes the recorded frame.
                    /// </summary>
                    public static long DesktopSecondHalfBusyStops => Interlocked.Read(ref _desktopSecondHalfBusyStops);

                    /// <summary>Counts one split desktop second half that stopped with a busy swapchain image.</summary>
                    public static void RecordDesktopSecondHalfBusyStop()
                        => Interlocked.Increment(ref _desktopSecondHalfBusyStops);

                    /// <summary>
                    /// Total count of window render circuit-breaker trips since start. Each trip
                    /// stops rendering of a window for its hold time after a render-callback
                    /// exception. The count is cumulative and does not depend on <see cref="EnableTracking"/>.
                    /// </summary>
                    public static long RenderCircuitBreakerTrips => Interlocked.Read(ref _renderCircuitBreakerTrips);

                    /// <summary>Counts one window render circuit-breaker trip.</summary>
                    internal static void RecordRenderCircuitBreakerTrip()
                        => Interlocked.Increment(ref _renderCircuitBreakerTrips);

                    public static int SkippedCollectFrames => _lastFrameSkippedCollectFrames;
                    public static int StaleCollectReuseFrames => _lastFrameStaleCollectReuseFrames;
                    public static double FramePackageProductionMs
                        => StopwatchTicksToMilliseconds(_lastFramePackageProductionTicks);
                    public static double FramePackagePublicationMs
                        => StopwatchTicksToMilliseconds(_lastFramePackagePublicationTicks);
                    public static double FramePackageValidationMs
                        => StopwatchTicksToMilliseconds(_lastFramePackageValidationTicks);
                    public static double FramePackageConsumptionMs
                        => StopwatchTicksToMilliseconds(_lastFramePackageConsumptionTicks);
                    public static int FramePackagesPrepared => _lastFramePackagesPrepared;
                    public static int FramePackagesPublished => _lastFramePackagesPublished;
                    public static int FramePackagesConsumed => _lastFramePackagesConsumed;
                    public static int FramePackagesPreparedLate => _lastFramePackagesPreparedLate;
                    public static int FramePackagesRejected => _lastFramePackagesRejected;
                    public static int FramePackageGenerationAge => _lastFramePackageGenerationAge;
                    public static string CollectVisibleLatePolicy
                        => RuntimeRenderingHostServices.FrameTiming.CollectVisibleLatePolicy;
                    public static ulong UpdateFrameId
                        => RuntimeRenderingHostServices.FrameTiming.UpdateFrameId;
                    public static ulong CollectFrameId
                        => RuntimeRenderingHostServices.FrameTiming.CollectFrameId;
                    public static ulong SwapFrameId
                        => RuntimeRenderingHostServices.FrameTiming.SwapFrameId;
                    public static ulong RenderFrameId => RuntimeEngine.Rendering.State.RenderFrameId;
                    public static ulong PresentFrameId
                        => RuntimeRenderingHostServices.FrameTiming.PresentFrameId;
                    public static long RequestedCollectGeneration
                        => RuntimeRenderingHostServices.FrameTiming.RequestedCollectGeneration;
                    public static long CompletedCollectGeneration
                        => RuntimeRenderingHostServices.FrameTiming.CompletedCollectGeneration;
                    public static long PublishedCollectGeneration
                        => RuntimeRenderingHostServices.FrameTiming.PublishedCollectGeneration;
                    public static long ConsumedCollectGeneration
                        => RuntimeRenderingHostServices.FrameTiming.ConsumedCollectGeneration;
                    public static long RequiredCollectGeneration
                        => RuntimeRenderingHostServices.FrameTiming.RequiredCollectGeneration;

                    internal static void RecordCollectWaitForRender(long stopwatchTicks)
                    {
                        AddPhase(ref _collectWaitForRenderTotalTicks, ref _collectWaitForRenderTotalCalls, stopwatchTicks);
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _collectWaitForRenderTicks, Math.Max(0L, stopwatchTicks));
                        if (stopwatchTicks > 0L)
                            Interlocked.Exchange(ref _collectWaitReason, (int)EFrameLifecycleWaitReason.WaitingForRenderThread);
                    }

                    internal static void RecordRenderWaitForCollect(long stopwatchTicks)
                    {
                        AddPhase(ref _renderWaitForCollectTotalTicks, ref _renderWaitForCollectTotalCalls, stopwatchTicks);
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _renderWaitForCollectTicks, Math.Max(0L, stopwatchTicks));
                        if (stopwatchTicks > 0L)
                            Interlocked.Exchange(ref _renderWaitReason, (int)EFrameLifecycleWaitReason.WaitingForCollectVisible);
                    }

                    /// <summary>
                    /// Records the part of a render-thread publication wait in which the collect thread
                    /// held the publication for <c>xrWaitFrame</c> (XR collect pacing).
                    /// </summary>
                    internal static void RecordRenderWaitForXrFrame(long stopwatchTicks)
                    {
                        AddPhase(ref _renderWaitForXrFrameTotalTicks, ref _renderWaitForXrFrameTotalCalls, stopwatchTicks);
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _renderWaitForXrFrameTicks, Math.Max(0L, stopwatchTicks));
                    }

                    internal static void RecordStaleCollectReuse()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _staleCollectReuseFrames);
                        Interlocked.Increment(ref _skippedCollectFrames);
                        Interlocked.Exchange(ref _renderWaitReason, (int)EFrameLifecycleWaitReason.ReusingPreviousVisibility);
                    }

                    internal static void RecordFramePackageProduction(long stopwatchTicks)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _framePackageProductionTicks, Math.Max(0L, stopwatchTicks));
                        Interlocked.Increment(ref _framePackagesPrepared);
                    }

                    internal static void RecordFramePackagePublication(long stopwatchTicks, bool preparedLate)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _framePackagePublicationTicks, Math.Max(0L, stopwatchTicks));
                        Interlocked.Increment(ref _framePackagesPublished);
                        if (preparedLate)
                            Interlocked.Increment(ref _framePackagesPreparedLate);
                    }

                    internal static void RecordFramePackageValidation(
                        long stopwatchTicks,
                        bool accepted,
                        int generationAge)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _framePackageValidationTicks, Math.Max(0L, stopwatchTicks));
                        Interlocked.Exchange(ref _framePackageGenerationAge, Math.Max(0, generationAge));
                        if (!accepted)
                            Interlocked.Increment(ref _framePackagesRejected);
                    }

                    internal static void RecordFramePackageConsumption(long stopwatchTicks)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Add(ref _framePackageConsumptionTicks, Math.Max(0L, stopwatchTicks));
                        Interlocked.Increment(ref _framePackagesConsumed);
                    }

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameCollectWaitForRenderTicks = Interlocked.Exchange(ref _collectWaitForRenderTicks, 0);
                        _lastFrameRenderWaitForCollectTicks = Interlocked.Exchange(ref _renderWaitForCollectTicks, 0);
                        _lastFrameRenderWaitForXrFrameTicks = Interlocked.Exchange(ref _renderWaitForXrFrameTicks, 0);
                        _lastFrameCollectWaitReason = Interlocked.Exchange(ref _collectWaitReason, 0);
                        _lastFrameRenderWaitReason = Interlocked.Exchange(ref _renderWaitReason, 0);
                        _lastFrameSkippedCollectFrames = Interlocked.Exchange(ref _skippedCollectFrames, 0);
                        _lastFrameStaleCollectReuseFrames = Interlocked.Exchange(ref _staleCollectReuseFrames, 0);
                        _lastFramePackageProductionTicks = Interlocked.Exchange(ref _framePackageProductionTicks, 0);
                        _lastFramePackagePublicationTicks = Interlocked.Exchange(ref _framePackagePublicationTicks, 0);
                        _lastFramePackageValidationTicks = Interlocked.Exchange(ref _framePackageValidationTicks, 0);
                        _lastFramePackageConsumptionTicks = Interlocked.Exchange(ref _framePackageConsumptionTicks, 0);
                        _lastFramePackagesPrepared = Interlocked.Exchange(ref _framePackagesPrepared, 0);
                        _lastFramePackagesPublished = Interlocked.Exchange(ref _framePackagesPublished, 0);
                        _lastFramePackagesConsumed = Interlocked.Exchange(ref _framePackagesConsumed, 0);
                        _lastFramePackagesPreparedLate = Interlocked.Exchange(ref _framePackagesPreparedLate, 0);
                        _lastFramePackagesRejected = Interlocked.Exchange(ref _framePackagesRejected, 0);
                        _lastFramePackageGenerationAge = Interlocked.Exchange(ref _framePackageGenerationAge, 0);
                    }

                    private static double StopwatchTicksToMilliseconds(long ticks)
                        => RuntimeTiming.TicksToSecondsDouble(ticks) * 1000.0;
                }
            }
        }
    }
}
