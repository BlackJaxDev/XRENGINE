using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                /// <summary>
                /// Measures how fast the engine could produce VR frames if the VR runtime did not pace them.
                /// </summary>
                /// <remarks>
                /// <para>
                /// OpenXR paces every frame with <c>xrWaitFrame</c>, so the presented VR rate cannot be higher
                /// than the headset refresh rate. This class records the frame work that the runtime does not pace:
                /// the CPU time from the return of <c>xrWaitFrame</c> to the end of <c>xrEndFrame</c>.
                /// The blocking wait in <c>xrWaitFrame</c> is not part of this time.
                /// </para>
                /// <para>
                /// The implied rate is <c>1000 / work ms</c>. It is an estimate for one frame at a time. It does not
                /// include GPU work that continues after <c>xrEndFrame</c> returns.
                /// </para>
                /// <para>
                /// The recording methods do not allocate. They are safe to call on the render and pacing threads.
                /// </para>
                /// </remarks>
                public static class VrHeadroom
                {
                    /// <summary>The number of recent frames that the window statistics use.</summary>
                    public const int SampleWindowSize = 128;

                    private static readonly long[] s_cpuWorkTicks = new long[SampleWindowSize];
                    private static long s_cpuWorkSampleCount;
                    private static long s_lastCpuWorkTicks;
                    private static long s_displayPeriodNanoseconds;

                    /// <summary>The total number of frame work samples since process start.</summary>
                    public static long CpuWorkSampleCount => Volatile.Read(ref s_cpuWorkSampleCount);

                    /// <summary>The unpaced CPU work time of the most recent VR frame, in milliseconds.</summary>
                    public static double LastCpuWorkMs => TicksToMs(Volatile.Read(ref s_lastCpuWorkTicks));

                    /// <summary>
                    /// The display period that the VR runtime reported with the most recent frame, in milliseconds.
                    /// Zero when no VR runtime reported a period.
                    /// </summary>
                    public static double DisplayPeriodMs => Volatile.Read(ref s_displayPeriodNanoseconds) / 1_000_000.0;

                    /// <summary>The headset refresh rate that the VR runtime paces to, in hertz. Zero when unknown.</summary>
                    public static double DisplayRefreshHz
                    {
                        get
                        {
                            long periodNs = Volatile.Read(ref s_displayPeriodNanoseconds);
                            return periodNs > 0 ? 1_000_000_000.0 / periodNs : 0.0;
                        }
                    }

                    /// <summary>
                    /// Records the CPU work of one VR frame: the time from the return of the runtime frame wait
                    /// to the end of the frame submission.
                    /// </summary>
                    /// <param name="stopwatchTicks">The elapsed time, in <see cref="Stopwatch"/> ticks.</param>
                    public static void RecordCpuFrameWork(long stopwatchTicks)
                    {
                        if (!EnableTracking || stopwatchTicks <= 0)
                            return;

                        // One writer (the thread that ends the XR frame) owns the ring. A reader can see a
                        // partly updated window, which changes the statistics by at most one sample.
                        long sampleIndex = Volatile.Read(ref s_cpuWorkSampleCount);
                        s_cpuWorkTicks[(int)(sampleIndex % SampleWindowSize)] = stopwatchTicks;
                        Volatile.Write(ref s_lastCpuWorkTicks, stopwatchTicks);
                        Volatile.Write(ref s_cpuWorkSampleCount, sampleIndex + 1);
                    }

                    /// <summary>Records the display period that the VR runtime predicted for the next frame.</summary>
                    /// <param name="periodNanoseconds">The predicted display period, in nanoseconds.</param>
                    public static void RecordDisplayPeriod(long periodNanoseconds)
                    {
                        if (periodNanoseconds > 0)
                            Volatile.Write(ref s_displayPeriodNanoseconds, periodNanoseconds);
                    }

                    /// <summary>
                    /// Computes statistics for the most recent <see cref="SampleWindowSize"/> frames.
                    /// This method is for diagnostics readers. It does not allocate on the heap.
                    /// </summary>
                    public static VrHeadroomWindow GetWindow()
                    {
                        long count = Volatile.Read(ref s_cpuWorkSampleCount);
                        int sampleCount = (int)Math.Min(count, SampleWindowSize);
                        if (sampleCount <= 0)
                            return default;

                        Span<long> samples = stackalloc long[sampleCount];
                        for (int i = 0; i < sampleCount; i++)
                            samples[i] = s_cpuWorkTicks[i];
                        samples.Sort();

                        long total = 0;
                        for (int i = 0; i < sampleCount; i++)
                            total += samples[i];

                        double averageMs = TicksToMs(total / (double)sampleCount);
                        double medianMs = TicksToMs(samples[sampleCount / 2]);
                        double p95Ms = TicksToMs(samples[Math.Min(sampleCount - 1, (int)Math.Ceiling(sampleCount * 0.95) - 1)]);
                        double worstMs = TicksToMs(samples[sampleCount - 1]);
                        return new VrHeadroomWindow(sampleCount, averageMs, medianMs, p95Ms, worstMs);
                    }

                    /// <summary>Converts a frame time to an implied frame rate. Returns zero for a time that is not positive.</summary>
                    public static double ImpliedHz(double frameTimeMs)
                        => frameTimeMs > 0.0 && double.IsFinite(frameTimeMs) ? 1000.0 / frameTimeMs : 0.0;

                    private static double TicksToMs(double ticks)
                        => ticks * 1000.0 / Stopwatch.Frequency;
                }
            }
        }
    }
}
