using System.Diagnostics;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

public sealed class NetworkingTimingTests
{
    [Test]
    public void TickDeltaToSeconds_RemainsStableAtLargeAbsoluteTickValues()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 12L;
        long laterTicks = baseTicks + Stopwatch.Frequency * 3L / 2L;

        BaseNetworkingManager.TickDeltaToSeconds(laterTicks, baseTicks).ShouldBe(1.5d, 0.000000000001d);
    }

    [Test]
    public void HasElapsed_UsesTickComparisonsForLongSessionIntervals()
    {
        long baseTicks = Stopwatch.Frequency * 60L * 60L * 24L;
        long thresholdTicks = baseTicks + BaseNetworkingManager.SecondsToStopwatchTicks(3.0);
        long beforeThresholdTicks = thresholdTicks - 1L;

        BaseNetworkingManager.HasElapsed(beforeThresholdTicks, baseTicks, 3.0).ShouldBeFalse();
        BaseNetworkingManager.HasElapsed(thresholdTicks, baseTicks, 3.0).ShouldBeTrue();
    }

    [Test]
    public void WindowStartTicks_ProducesStableOneSecondCutoffAtLargeTickValues()
    {
        long nowTicks = Stopwatch.Frequency * 60L * 60L * 36L;
        long expectedWindowStart = nowTicks - Stopwatch.Frequency;

        BaseNetworkingManager.GetWindowStartTicks(nowTicks, 1.0).ShouldBe(expectedWindowStart);
    }
}