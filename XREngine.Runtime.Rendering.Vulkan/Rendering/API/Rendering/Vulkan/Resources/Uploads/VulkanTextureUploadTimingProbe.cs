using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Sub-phase timing probes for the Vulkan texture upload path. Lines go to the
/// texture-upload timing auxiliary log, which every build configuration writes.
/// Each probe allocates only when its call is at or over the threshold.
/// </summary>
internal static class VulkanTextureUploadTimingProbe
{
    /// <summary>
    /// Auxiliary log file for upload timing probes.
    /// </summary>
    internal const string LogFile = "texture-upload-timing.log";

    /// <summary>
    /// A probed call writes a line only when its total time is at or over this value.
    /// </summary>
    internal const double ThresholdMilliseconds = 4.0;

    private static long s_recordStateTicks;
    private static long s_recordStateCalls;

    // Sub-phases of the committed publication tail, filled on the render thread
    // and read by the publication line of the same call.
    [ThreadStatic] internal static double CompleteMarkPublishedMs;
    [ThreadStatic] internal static double CompleteRetireStagingMs;
    [ThreadStatic] internal static double CompleteLatencyMs;
    [ThreadStatic] internal static double CompleteStateMs;
    [ThreadStatic] internal static double CompleteCallbacksMs;

    // Sub-phases of the wrapper-side descriptor publication.
    [ThreadStatic] internal static bool DescriptorDetailValid;
    [ThreadStatic] internal static double DescriptorLockAndCheckMs;
    [ThreadStatic] internal static double DescriptorActivateMs;
    [ThreadStatic] internal static double DescriptorCommitMs;
    [ThreadStatic] internal static double DescriptorWrapperSwapMs;
    [ThreadStatic] internal static double DescriptorRetireMs;

    /// <summary>Total time spent in RecordState since process start, in milliseconds.</summary>
    internal static double RecordStateTotalMilliseconds
        => Volatile.Read(ref s_recordStateTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Total number of RecordState calls since process start.</summary>
    internal static long RecordStateCalls
        => Volatile.Read(ref s_recordStateCalls);

    internal static void AddRecordState(long elapsedTicks)
    {
        Interlocked.Add(ref s_recordStateTicks, elapsedTicks);
        Interlocked.Increment(ref s_recordStateCalls);
    }

    internal static double Ms(long start, long end)
        => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    internal static void Write(string message)
        => Debug.WriteAuxiliaryLog(LogFile, message);
}
