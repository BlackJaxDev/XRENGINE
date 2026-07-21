using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkContractTests
{
    private static readonly PhysicsChainBenchmarkSettleSnapshot ReadySnapshot = new(
        ChainCount: 1_000,
        CapacitySignature: 0x1234,
        PendingPipelineCompilationCount: 0,
        PendingUploadCount: 0,
        RendererCount: 1_000);

    [Test]
    public void Configuration_DefaultsRequireStableWarmupAndLongSteadyStateSample()
    {
        var configuration = new PhysicsChainBenchmarkConfiguration();

        Should.NotThrow(configuration.Validate);
        configuration.StableFrameCount.ShouldBe(30);
        configuration.MinimumSampleFrameCount.ShouldBe(1_000);
        configuration.MinimumDurationSeconds.ShouldBe(30.0);
        configuration.DeterministicSeed.ShouldBe(0x58524348);
    }

    [TestCase(0, 1_000, 10.0)]
    [TestCase(30, 0, 10.0)]
    [TestCase(30, 1_000, 0.0)]
    [TestCase(30, 1_000, double.NaN)]
    public void Configuration_RejectsInvalidTimingPolicy(int stableFrames, int sampleFrames, double durationSeconds)
    {
        var configuration = new PhysicsChainBenchmarkConfiguration
        {
            StableFrameCount = stableFrames,
            MinimumSampleFrameCount = sampleFrames,
            MinimumDurationSeconds = durationSeconds,
        };

        Should.Throw<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Test]
    public void Matrix_ExposesCanonicalScaleAxes()
    {
        PhysicsChainBenchmarkMatrix.ChainCounts.ToArray().ShouldBe([100, 500, 1_000, 2_000, 5_000, 10_000]);
        PhysicsChainBenchmarkMatrix.DynamicSegmentCounts.ToArray().ShouldBe([4, 8, 16, 32]);
        PhysicsChainBenchmarkMatrix.ActiveRatios.ToArray().ShouldBe([1.0f, 0.5f, 0.1f]);
        PhysicsChainBenchmarkMatrix.FixedSimulationRates.ToArray().ShouldBe([30, 60, 90, 120]);
    }

    [Test]
    public void SettleGate_RequiresConsecutiveIdenticalReadySnapshots()
    {
        var gate = new PhysicsChainBenchmarkSettleGate(requiredStableFrames: 3);

        gate.Observe(ReadySnapshot).ShouldBeFalse();
        gate.Observe(ReadySnapshot).ShouldBeFalse();
        gate.Observe(ReadySnapshot).ShouldBeTrue();
        gate.IsSettled.ShouldBeTrue();
    }

    [Test]
    public void SettleGate_PendingWorkAndResourceChangesRestartStability()
    {
        var gate = new PhysicsChainBenchmarkSettleGate(requiredStableFrames: 2);
        PhysicsChainBenchmarkSettleSnapshot pending = ReadySnapshot with { PendingUploadCount = 1 };
        PhysicsChainBenchmarkSettleSnapshot resized = ReadySnapshot with { CapacitySignature = 0x5678 };

        gate.Observe(ReadySnapshot).ShouldBeFalse();
        gate.Observe(pending).ShouldBeFalse();
        gate.StableFrameCount.ShouldBe(0);
        gate.Observe(ReadySnapshot).ShouldBeFalse();
        gate.Observe(resized).ShouldBeFalse();
        gate.Observe(resized).ShouldBeTrue();
    }

    [Test]
    public void RunController_ExcludesSettleFramesAndRequiresDurationAndSampleCount()
    {
        var configuration = new PhysicsChainBenchmarkConfiguration
        {
            StableFrameCount = 2,
            MinimumSampleFrameCount = 1_000,
            MinimumDurationSeconds = 10.0,
        };
        var run = new PhysicsChainBenchmarkRunController(configuration);

        run.ObserveSettleFrame(ReadySnapshot).ShouldBe(PhysicsChainBenchmarkRunState.Settling);
        run.ObserveSettleFrame(ReadySnapshot).ShouldBe(PhysicsChainBenchmarkRunState.Measuring);
        run.ObserveMeasurement(elapsedSeconds: 10.0, sampleFrameCount: 999)
            .ShouldBe(PhysicsChainBenchmarkRunState.Measuring);
        run.ObserveMeasurement(elapsedSeconds: 9.999, sampleFrameCount: 1_000)
            .ShouldBe(PhysicsChainBenchmarkRunState.Measuring);
        run.ObserveMeasurement(elapsedSeconds: 10.0, sampleFrameCount: 1_000)
            .ShouldBe(PhysicsChainBenchmarkRunState.Complete);
    }

    [Test]
    public void RunController_BoundsASettlePhaseThatNeverStabilizes()
    {
        var configuration = new PhysicsChainBenchmarkConfiguration { StableFrameCount = 2 };
        var run = new PhysicsChainBenchmarkRunController(configuration, maximumSettleFrameCount: 3);

        for (int i = 0; i < 3; ++i)
        {
            PhysicsChainBenchmarkSettleSnapshot changing = ReadySnapshot with { CapacitySignature = i };
            run.ObserveSettleFrame(changing);
        }

        run.State.ShouldBe(PhysicsChainBenchmarkRunState.SettleTimedOut);
    }

    [Test]
    public void FrameStatistics_ReportsNearestRankPercentilesAndExtrema()
    {
        double[] samples = Enumerable.Range(1, 100).Select(static value => (double)value).ToArray();

        PhysicsChainBenchmarkFrameStatistics statistics = PhysicsChainBenchmarkFrameStatistics.Calculate(samples);

        statistics.SampleCount.ShouldBe(100);
        statistics.MinimumMilliseconds.ShouldBe(1.0);
        statistics.MeanMilliseconds.ShouldBe(50.5);
        statistics.P50Milliseconds.ShouldBe(50.0);
        statistics.P95Milliseconds.ShouldBe(95.0);
        statistics.P99Milliseconds.ShouldBe(99.0);
        statistics.MaximumMilliseconds.ShouldBe(100.0);
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(-0.001)]
    public void FrameStatistics_RejectsInvalidSamples(double invalidSample)
        => Should.Throw<ArgumentOutOfRangeException>(() =>
            PhysicsChainBenchmarkFrameStatistics.Calculate([1.0, invalidSample]));
}
