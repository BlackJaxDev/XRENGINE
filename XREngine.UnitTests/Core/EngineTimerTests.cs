using System.Diagnostics;
using NUnit.Framework;
using Shouldly;
using XREngine.Timers;

namespace XREngine.UnitTests.Core;

public sealed class EngineTimerTests
{
    [Test]
    public void ConversionHelpers_RoundTripStopwatchAndTimeSpanTicks()
    {
        long stopwatchTicks = Stopwatch.Frequency * 7L / 3L;

        long timeSpanTicks = EngineTimer.StopwatchTicksToTimeSpanTicks(stopwatchTicks);
        long roundTripTicks = EngineTimer.TimeSpanTicksToStopwatchTicks(timeSpanTicks);

        timeSpanTicks.ShouldBe((long)Math.Round(stopwatchTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
        roundTripTicks.ShouldBeInRange(stopwatchTicks - 1L, stopwatchTicks + 1L);
    }

    [Test]
    public void ConversionHelpers_ExposeDoubleAndFloatSeconds()
    {
        long ticks = Stopwatch.Frequency * 5L / 4L;

        EngineTimer.TicksToSecondsDouble(ticks).ShouldBe(1.25d, 0.000000000001d);
        EngineTimer.TicksToSeconds(ticks).ShouldBe(1.25f, 0.000001f);
        EngineTimer.SecondsToStopwatchTicks(1.25d).ShouldBe(ticks);
    }

    [Test]
    public void DeltaManager_DeltaPropertySynchronizesWithTickStorage()
    {
        var manager = new EngineTimer.DeltaManager();

        manager.Delta = 0.25f;

        manager.DeltaTicks.ShouldBe((long)Math.Round(0.25 * Stopwatch.Frequency));
        manager.Delta.ShouldBe(0.25f, 0.000001f);
    }

    [Test]
    public void DeltaManager_TickPropertiesExposeSecondViews()
    {
        var manager = new EngineTimer.DeltaManager();
        long timestampTicks = Stopwatch.Frequency * 3L / 2L;
        long elapsedTicks = Stopwatch.Frequency / 8L;

        manager.LastTimestampTicks = timestampTicks;
        manager.ElapsedTicks = elapsedTicks;

        manager.LastTimestamp.ShouldBe((float)(timestampTicks / (double)Stopwatch.Frequency), 0.000001f);
        manager.ElapsedTime.ShouldBe((float)(elapsedTicks / (double)Stopwatch.Frequency), 0.000001f);
    }

    [Test]
    public void TargetPeriodsAndFixedDeltaRoundTripThroughTickBacking()
    {
        var timer = new EngineTimer();

        timer.FixedUpdateDelta = 1.0f / 120.0f;
        timer.TargetUpdateFrequency = 144.0f;
        timer.TargetRenderPeriod = 1.0f / 90.0f;

        timer.FixedUpdateDelta.ShouldBe(1.0f / 120.0f, 0.000001f);
        timer.FixedUpdateFrequency.ShouldBe(120.0f, 0.001f);
        timer.TargetUpdateFrequency.ShouldBe(144.0f, 0.001f);
        timer.TargetRenderPeriod.ShouldBe(1.0f / 90.0f, 0.000001f);
        timer.TargetRenderFrequency.ShouldBe(90.0f, 0.001f);
    }

    [Test]
    public void EngineTimeAccessors_ExposeTimerTicksAndFrequency()
    {
        Engine.ElapsedTicks.ShouldBe(Engine.Time.ElapsedTicks);
        Engine.ElapsedTicks.ShouldBe(Engine.Time.Timer.TimeTicks());
        Engine.StopwatchFrequency.ShouldBe(Stopwatch.Frequency);
        Engine.Time.StopwatchFrequency.ShouldBe(Stopwatch.Frequency);
    }

    [Test]
    public void EngineTimeAccessors_ExposeTickBasedDeltas()
    {
        var update = Engine.Time.Timer.Update;
        long previousDeltaTicks = update.DeltaTicks;
        float previousDilation = update.Dilation;

        try
        {
            update.DeltaTicks = Stopwatch.Frequency / 120L;
            update.Dilation = 0.5f;

            Engine.UndilatedDeltaTicks.ShouldBe(update.DeltaTicks);
            Engine.Time.UndilatedDeltaTicks.ShouldBe(update.DeltaTicks);
            Engine.DeltaTicks.ShouldBe((long)Math.Round(update.DeltaTicks * 0.5d));
            Engine.Time.DeltaTicks.ShouldBe(Engine.DeltaTicks);
            Engine.FixedDeltaTicks.ShouldBe(Engine.Time.Timer.FixedUpdateDeltaTicks);
            Engine.Time.FixedDeltaTicks.ShouldBe(Engine.FixedDeltaTicks);
        }
        finally
        {
            update.DeltaTicks = previousDeltaTicks;
            update.Dilation = previousDilation;
        }
    }
}