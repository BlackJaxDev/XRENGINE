using System;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                private static int _vrLeftEyeDraws;
                private static int _vrRightEyeDraws;
                private static int _lastFrameVrLeftEyeDraws;
                private static int _lastFrameVrRightEyeDraws;
                private static int _vrLeftEyeVisible;
                private static int _vrRightEyeVisible;
                private static int _lastFrameVrLeftEyeVisible;
                private static int _lastFrameVrRightEyeVisible;
                private static long _vrLeftWorkerBuildTimeTicks;
                private static long _vrRightWorkerBuildTimeTicks;
                private static long _lastFrameVrLeftWorkerBuildTimeTicks;
                private static long _lastFrameVrRightWorkerBuildTimeTicks;
                private static long _vrRenderSubmitTimeTicks;
                private static long _lastFrameVrRenderSubmitTimeTicks;
                private static long _vrXrWaitFrameBlockTimeTicks;
                private static long _lastFrameVrXrWaitFrameBlockTimeTicks;
                private static long _vrXrEndFrameSubmitTimeTicks;
                private static long _lastFrameVrXrEndFrameSubmitTimeTicks;
                private static long _vrXrPredictedToLatePoseDeltaMillimetersBits;
                private static long _lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits;
                private static long _vrXrPredictedToLatePoseDeltaDegreesBits;
                private static long _lastFrameVrXrPredictedToLatePoseDeltaDegreesBits;
                private static long _vrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                private static long _lastFrameVrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                private static int _vrXrMissedDeadlineFrames;
                private static int _lastFrameVrXrMissedDeadlineFrames;
                private static int _vrXrTrackingLossFrames;
                private static int _lastFrameVrXrTrackingLossFrames;
                private static long _vrXrRelocatePredictedTimeTicks;
                private static long _lastFrameVrXrRelocatePredictedTimeTicks;
                private static long _vrXrCollectFrustumExpansionDegreesBits;
                private static long _lastFrameVrXrCollectFrustumExpansionDegreesBits;
                private static long _vrXrPacingThreadIdleTimeTicks;
                private static long _lastFrameVrXrPacingThreadIdleTimeTicks;
                private static int _vrXrPacingHandoffStalls;
                private static int _lastFrameVrXrPacingHandoffStalls;

                // Render-matrix stats use a separate swap cycle aligned with SwapBuffers phase.

                public static int VrLeftEyeDraws => _lastFrameVrLeftEyeDraws;
                public static int VrRightEyeDraws => _lastFrameVrRightEyeDraws;
                public static int VrLeftEyeVisible => _lastFrameVrLeftEyeVisible;
                public static int VrRightEyeVisible => _lastFrameVrRightEyeVisible;
                public static double VrLeftWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrLeftWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRightWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrRightWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRenderSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrRenderSubmitTimeTicks).TotalMilliseconds;
                public static double VrXrWaitFrameBlockTimeMs => TimeSpan.FromTicks(_lastFrameVrXrWaitFrameBlockTimeTicks).TotalMilliseconds;
                public static double VrXrEndFrameSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrXrEndFrameSubmitTimeTicks).TotalMilliseconds;
                public static double VrXrPredictedToLatePoseDeltaMillimeters => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits);
                public static double VrXrPredictedToLatePoseDeltaDegrees => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedToLatePoseDeltaDegreesBits);
                public static double VrXrPredictedDisplayLeadTimeMs => BitConverter.Int64BitsToDouble(_lastFrameVrXrPredictedDisplayLeadTimeMsBits);
                public static int VrXrMissedDeadlineFrames => _lastFrameVrXrMissedDeadlineFrames;
                public static int VrXrTrackingLossFrames => _lastFrameVrXrTrackingLossFrames;
                public static double VrXrRelocatePredictedTimeMs => TimeSpan.FromTicks(_lastFrameVrXrRelocatePredictedTimeTicks).TotalMilliseconds;
                public static double VrXrCollectFrustumExpansionDegrees => BitConverter.Int64BitsToDouble(_lastFrameVrXrCollectFrustumExpansionDegreesBits);
                public static double VrXrPacingThreadIdleTimeMs => TimeSpan.FromTicks(_lastFrameVrXrPacingThreadIdleTimeTicks).TotalMilliseconds;
                public static int VrXrPacingHandoffStalls => _lastFrameVrXrPacingHandoffStalls;

                public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeDraws, (int)Math.Min(leftDraws, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeDraws, (int)Math.Min(rightDraws, int.MaxValue));
                }

                public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeVisible, (int)Math.Min(leftVisible, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeVisible, (int)Math.Min(rightVisible, int.MaxValue));
                }

                public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, leftBuildTime.Ticks);
                    Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, rightBuildTime.Ticks);
                }

                public static void RecordVrRenderSubmitTime(TimeSpan submitTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, submitTime.Ticks);
                }

                public static void RecordVrXrWaitFrameBlockTime(TimeSpan waitTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrWaitFrameBlockTimeTicks, waitTime.Ticks);
                }

                public static void RecordVrXrEndFrameSubmitTime(TimeSpan submitTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrEndFrameSubmitTimeTicks, submitTime.Ticks);
                }

                public static void RecordVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaMillimetersBits, BitConverter.DoubleToInt64Bits(millimeters));
                    Interlocked.Exchange(ref _vrXrPredictedToLatePoseDeltaDegreesBits, BitConverter.DoubleToInt64Bits(degrees));
                }

                public static void RecordVrXrPredictedDisplayLeadTime(double leadTimeMs)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrPredictedDisplayLeadTimeMsBits, BitConverter.DoubleToInt64Bits(leadTimeMs));
                }

                public static void RecordVrXrMissedDeadlineFrame()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vrXrMissedDeadlineFrames);
                }

                public static void RecordVrXrTrackingLossFrame()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vrXrTrackingLossFrames);
                }

                public static void RecordVrXrRelocatePredictedTime(TimeSpan elapsed)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrRelocatePredictedTimeTicks, elapsed.Ticks);
                }

                public static void RecordVrXrCollectFrustumExpansionDegrees(double degrees)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrXrCollectFrustumExpansionDegreesBits, BitConverter.DoubleToInt64Bits(degrees));
                }

                /// <summary>
                /// Records the time the OpenXR pacing thread spent idle waiting for the render thread's frame-submit signal.
                /// Accumulated across pacing-thread wakes in a single frame.
                /// </summary>
                public static void RecordVrXrPacingThreadIdleTime(TimeSpan elapsed)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Add(ref _vrXrPacingThreadIdleTimeTicks, elapsed.Ticks);
                }

                /// <summary>
                /// Records that the render thread had to stall because the pacing thread had not yet published the
                /// next OpenXR frame (predicted views) before the engine wanted to render.
                /// </summary>
                public static void RecordVrXrPacingHandoffStall()
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vrXrPacingHandoffStalls);
                }
            }
        }
    }
}
