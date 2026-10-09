using System.Diagnostics;

namespace XREngine;

/// <summary>
/// Monotonic elapsed time and execution count for one frame-loop phase.
/// </summary>
/// <param name="StopwatchTicks">Total elapsed <see cref="Stopwatch"/> ticks recorded for the phase.</param>
/// <param name="Calls">Number of recorded phase executions.</param>
public readonly record struct FrameLifecyclePhaseTotal(long StopwatchTicks, long Calls)
{
    /// <summary>Gets the total elapsed time in milliseconds.</summary>
    public double TotalMilliseconds => StopwatchTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Returns saturated non-negative deltas from <paramref name="baseline"/>.</summary>
    public FrameLifecyclePhaseTotal DeltaFrom(FrameLifecyclePhaseTotal baseline)
        => new(
            Math.Max(0L, StopwatchTicks - baseline.StopwatchTicks),
            Math.Max(0L, Calls - baseline.Calls));
}
