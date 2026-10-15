using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Optional cumulative timers for the parts of Advanced preparation extraction. Set
/// <c>XRE_ADVANCED_EXTRACTION_SUBSTAGE_TIMERS=1</c> before startup to enable them. When the
/// variable is not set, every call returns at once.
/// </summary>
/// <remarks>
/// Only the render thread writes the counters, through the extractor. A reader on another thread
/// can see values from different frames; read two captures inside a window and use the difference.
/// Each enabled row adds about eight timestamp reads.
/// </remarks>
internal static class AdvancedExtractionSubStageTimers
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.AdvancedExtractionSubStageTimers) == "1";

    private static readonly long[] s_ticks = new long[(int)EAdvancedExtractionSubStage.Count];
    private static long s_rows;
    private static long s_builds;

    /// <summary>Returns the start timestamp of a measured part, or zero when the timers are off.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start()
        => Enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Adds the time since <paramref name="mark"/> to <paramref name="stage"/> and moves the mark.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mark(ref long mark, EAdvancedExtractionSubStage stage)
    {
        if (!Enabled)
            return;

        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref s_ticks[(int)stage], s_ticks[(int)stage] + (now - mark));
        mark = now;
    }

    /// <summary>Counts one extracted row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountRow()
    {
        if (Enabled)
            Volatile.Write(ref s_rows, s_rows + 1);
    }

    /// <summary>Counts one extraction build.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountBuild()
    {
        if (Enabled)
            Volatile.Write(ref s_builds, s_builds + 1);
    }

    /// <summary>Captures the cumulative counters.</summary>
    public static AdvancedExtractionSubStageTimings Capture()
        => new(
            Enabled,
            Volatile.Read(ref s_builds),
            Volatile.Read(ref s_rows),
            Milliseconds(EAdvancedExtractionSubStage.PrePass),
            Milliseconds(EAdvancedExtractionSubStage.RouteAndCover),
            Milliseconds(EAdvancedExtractionSubStage.GeometryAndCandidate),
            Milliseconds(EAdvancedExtractionSubStage.MeshPreparation),
            Milliseconds(EAdvancedExtractionSubStage.Pose),
            Milliseconds(EAdvancedExtractionSubStage.HistoryAndArena),
            Milliseconds(EAdvancedExtractionSubStage.ScheduleAndJob),
            Milliseconds(EAdvancedExtractionSubStage.Payload),
            Milliseconds(EAdvancedExtractionSubStage.AdmissionVerdicts));

    private static double Milliseconds(EAdvancedExtractionSubStage stage)
        => Volatile.Read(ref s_ticks[(int)stage]) * 1000.0 / Stopwatch.Frequency;
}
