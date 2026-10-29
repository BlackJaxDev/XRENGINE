using System.Diagnostics;
using System.Threading;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Runtime.Platform.Desktop;
using XREngine.Timers;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[NonParallelizable]
public sealed class EngineTimerTests
{
    private static readonly string[] AssetPathVariables =
    [
        XREngineEnvironmentVariables.EngineAssetsPath,
        XREngineEnvironmentVariables.GameAssetsPath,
        XREngineEnvironmentVariables.GameCachePath,
        XREngineEnvironmentVariables.GameMetadataPath,
    ];
    private string?[] _previousAssetPaths = null!;
    private IAssetFileSystem? _previousFiles;
    private IRuntimeFileDiscovery? _previousDiscovery;

    [OneTimeSetUp]
    public void ConfigureNativeHost()
    {
        _previousAssetPaths = AssetPathVariables.Select(Environment.GetEnvironmentVariable).ToArray();
        _previousFiles = AssetFileSystemServices.Current;
        _previousDiscovery = RuntimeFileDiscoveryServices.Current;
        try
        {
            ThreadedWorkerBackend.EnsureRegistered();
            string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "engine-timer-host-" + Guid.NewGuid().ToString("N"));
            string[] directories = ["Engine", "Assets", "Cache", "Metadata"];
            for (int index = 0; index < AssetPathVariables.Length; index++)
            {
                string path = Path.Combine(root, directories[index]);
                Directory.CreateDirectory(path);
                Environment.SetEnvironmentVariable(AssetPathVariables[index], path);
            }
            AssetFileSystemServices.Current = new DesktopAssetFileSystem();
            // Initialize on the test thread so a missing host capability reports
            // a fixture failure instead of terminating a background loop thread.
            RuntimeHelpers.RunClassConstructor(typeof(Engine).TypeHandle);
        }
        catch
        {
            RestoreNativeHost();
            throw;
        }
    }

    [OneTimeTearDown]
    public void RestoreNativeHost()
    {
        for (int index = 0; index < AssetPathVariables.Length; index++)
            Environment.SetEnvironmentVariable(AssetPathVariables[index], _previousAssetPaths[index]);
        AssetFileSystemServices.Current = _previousFiles;
        RuntimeFileDiscoveryServices.Current = _previousDiscovery;
    }

    [Test]
    public void ConversionHelpers_RoundTripStopwatchAndTimeSpanTicks()
    {
        long stopwatchTicks = Stopwatch.Frequency * 7L / 3L;

        long timeSpanTicks = EngineTimer.StopwatchTicksToTimeSpanTicks(stopwatchTicks);
        long roundTripTicks = EngineTimer.TimeSpanTicksToStopwatchTicks(timeSpanTicks);

        timeSpanTicks.ShouldBe((long)Math.Round(stopwatchTicks * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
        // Conversion rounds once in each clock. A TimeSpan tick can span many stopwatch ticks.
        long maximumRoundingError = (long)Math.Ceiling(Stopwatch.Frequency / (2.0 * TimeSpan.TicksPerSecond) + 0.5);
        roundTripTicks.ShouldBeInRange(stopwatchTicks - maximumRoundingError, stopwatchTicks + maximumRoundingError);
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
    public void CollectVisibleLatePolicy_DefaultsToBlockUntilFresh()
    {
        string? previous = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy);
        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy, null);
            var timer = new EngineTimer();

            timer.CollectVisibleLatePolicy.ShouldBe(ECollectVisibleLatePolicy.BlockUntilFresh);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy, previous);
        }
    }

    [TestCase("invalid", ECollectVisibleLatePolicy.BlockUntilFresh)]
    [TestCase("999", ECollectVisibleLatePolicy.BlockUntilFresh)]
    [TestCase("block", ECollectVisibleLatePolicy.BlockUntilFresh)]
    [TestCase("fresh", ECollectVisibleLatePolicy.BlockUntilFresh)]
    [TestCase("reuse", ECollectVisibleLatePolicy.ReusePreviousVisibility)]
    [TestCase("stale", ECollectVisibleLatePolicy.ReusePreviousVisibility)]
    public void CollectVisibleLatePolicy_RequiresExplicitValidStaleReuse(
        string configuredValue,
        ECollectVisibleLatePolicy expected)
    {
        string? previous = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy);
        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy, configuredValue);
            var timer = new EngineTimer();

            timer.CollectVisibleLatePolicy.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.CollectVisibleLatePolicy, previous);
        }
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

    [Test]
    [NonParallelizable]
    public void StopAndWait_IsBoundedWhenAnUpdateCallbackIsBlocked()
    {
        using var updateEntered = new ManualResetEventSlim(false);
        using var releaseUpdate = new ManualResetEventSlim(false);
        var timer = new EngineTimer
        {
            TargetUpdateFrequency = 120.0f,
        };

        void BlockUpdate()
        {
            updateEntered.Set();
            releaseUpdate.Wait();
        }

        timer.UpdateFrame += BlockUpdate;
        timer.RunGameLoop();
        try
        {
            updateEntered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();

            var waitDuration = Stopwatch.StartNew();
            timer.StopAndWait(TimeSpan.FromMilliseconds(50)).ShouldBeFalse();
            waitDuration.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
            Should.Throw<InvalidOperationException>(timer.RunGameLoop);
            Should.Throw<InvalidOperationException>(() => timer.BeginExplicitFrame(0.01f));
        }
        finally
        {
            releaseUpdate.Set();
            timer.StopAndWait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
            timer.UpdateFrame -= BlockUpdate;
        }

        timer.RunGameLoop();
        timer.StopAndWait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
    }
}
