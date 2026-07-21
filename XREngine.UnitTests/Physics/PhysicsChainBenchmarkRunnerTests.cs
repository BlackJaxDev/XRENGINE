using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkRunnerTests
{
    [Test]
    public void RunnerExcludesSettleFramesAndCapturesBoundedRawSamples()
    {
        var configuration = new PhysicsChainBenchmarkConfiguration
        {
            StableFrameCount = 2,
            MinimumSampleFrameCount = 5,
            MinimumDurationSeconds = 0.000001,
            DeterministicSeed = 42,
        };
        var runner = new PhysicsChainBenchmarkRunner(configuration, maximumSampleCount: 32);
        var scenario = new FakeScenario();

        PhysicsChainBenchmarkEvidence evidence = runner.Run(
            scenario,
            CreateWorkItem(),
            new PhysicsChainBenchmarkRunPolicy(),
            CreateEnvironment(),
            default);

        evidence.Result.FrameTimesMilliseconds.Length.ShouldBeGreaterThanOrEqualTo(5);
        evidence.Result.FrameStatistics.SampleCount.ShouldBe(evidence.Result.FrameTimesMilliseconds.Length);
        evidence.Result.GpuFrameTimesMilliseconds.ShouldBe([0.5, 0.75, 1.0]);
        evidence.Result.GpuFrameStatistics!.Value.P95Milliseconds.ShouldBe(1.0);
        evidence.Result.DeterministicSeed.ShouldBe(42);
        evidence.Result.SettleFrameCount.ShouldBeGreaterThanOrEqualTo(2);
        scenario.TeardownCount.ShouldBe(1);
        evidence.CpuMillisecondsPerActiveChain.ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Test]
    public void ScenarioFailureStillTearsDownExactlyOnce()
    {
        var runner = new PhysicsChainBenchmarkRunner(new PhysicsChainBenchmarkConfiguration
        {
            StableFrameCount = 1,
            MinimumSampleFrameCount = 1,
            MinimumDurationSeconds = 0.000001,
        });
        var scenario = new FakeScenario { ThrowOnFrame = true };

        Should.Throw<InvalidOperationException>(() => runner.Run(
            scenario,
            CreateWorkItem(),
            new PhysicsChainBenchmarkRunPolicy(),
            CreateEnvironment(),
            default));
        scenario.TeardownCount.ShouldBe(1);
    }

    private static PhysicsChainBenchmarkWorkItem CreateWorkItem()
        => new(
            0,
            new PhysicsChainBenchmarkCase(
                100,
                8,
                PhysicsChainBenchmarkTopology.Linear,
                PhysicsChainBenchmarkColliderScenario.None,
                PhysicsChainBenchmarkColliderOwnership.Shared,
                PhysicsChainBenchmarkActivityProfile.Active100,
                PhysicsChainBenchmarkRenderingMode.None,
                PhysicsChainBenchmarkExecutionMode.CpuStrict,
                PhysicsChainBenchmarkReadbackMode.Disabled,
                PhysicsChainBenchmarkRenderBackend.OpenGL,
                60),
            PhysicsChainBenchmarkMeasurementKind.SteadyState,
            0);

    private static PhysicsChainBenchmarkEnvironment CreateEnvironment()
        => new()
        {
            CpuModel = "test-cpu",
            PhysicalCoreCount = 8,
            LogicalCoreCount = 16,
            CoreTopology = "test",
            MemoryBytes = 16L * 1024 * 1024 * 1024,
            MemoryConfiguration = "test",
            GpuModel = "test-gpu",
            GpuDriver = "1",
            WindowsVersion = "test",
            PowerMode = "test",
            RenderBackend = PhysicsChainBenchmarkRenderBackend.OpenGL,
            Width = 1920,
            Height = 1080,
            RefreshRateHz = 60,
            VSyncEnabled = false,
            BuildConfiguration = "Release",
            Commit = "test",
        };

    private sealed class FakeScenario : IPhysicsChainBenchmarkScenario
    {
        public bool ThrowOnFrame { get; init; }
        public int TeardownCount { get; private set; }

        public void Setup(
            in PhysicsChainBenchmarkCase matrixCase,
            PhysicsChainBenchmarkMeasurementKind measurementKind,
            in PhysicsChainBenchmarkDeterministicScenario deterministicScenario)
        {
        }

        public PhysicsChainBenchmarkSettleSnapshot RunFrame()
        {
            if (ThrowOnFrame)
                throw new InvalidOperationException("synthetic failure");
            return new PhysicsChainBenchmarkSettleSnapshot(100, 1L, 0, 0, 0);
        }

        public PhysicsChainBenchmarkScenarioMetrics CaptureMetrics()
            => new()
            {
                Population = new PhysicsChainBenchmarkPopulationMetrics
                {
                    ActiveChains = 100,
                    ActiveParticles = 800,
                },
                GpuFrameTimesMilliseconds = [0.5, 0.75, 1.0],
            };

        public void Teardown() => ++TeardownCount;
    }
}
