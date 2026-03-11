using System;
using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Timers;

namespace XREngine.UnitTests.Core;

public sealed class MediaTimingTests
{
    [Test]
    public void MediaClockConversion_IsMonotonicForIncreasingStopwatchTicks()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 24L;
        long first = EngineTimer.StopwatchTicksToTimeSpanTicks(baseTicks);
        long second = EngineTimer.StopwatchTicksToTimeSpanTicks(baseTicks + 1L);
        long third = EngineTimer.StopwatchTicksToTimeSpanTicks(baseTicks + Stopwatch.Frequency / 10L);

        second.ShouldBeGreaterThanOrEqualTo(first);
        third.ShouldBeGreaterThan(second);
    }

    [TestCase(0.0)]
    [TestCase(0.25)]
    [TestCase(1.5)]
    [TestCase(60.0)]
    public void MediaClockConversion_MatchesLegacyFloatPathForShortRuns(double seconds)
    {
        long stopwatchTicks = EngineTimer.SecondsToStopwatchTicks(seconds);
        long convertedTicks = EngineTimer.StopwatchTicksToTimeSpanTicks(stopwatchTicks);
        long legacyTicks = (long)(EngineTimer.TicksToSeconds(stopwatchTicks) * TimeSpan.TicksPerSecond);

        convertedTicks.ShouldBeInRange(legacyTicks - 1L, legacyTicks + 1L);
    }
}