using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkSteadyStateInvariantTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RunnerRejectsCapacityChangesAndPendingUploadsDuringMeasurement(bool pendingUpload)
    {
        var runner = new PhysicsChainBenchmarkRunner(new PhysicsChainBenchmarkConfiguration
        {
            StableFrameCount = 2,
            MinimumSampleFrameCount = 1,
            MinimumDurationSeconds = 0.000001,
        });
        var scenario = new ChangingScenario(pendingUpload);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => runner.Run(
            scenario,
            CreateWorkItem(),
            new PhysicsChainBenchmarkRunPolicy(),
            CreateEnvironment(),
            default));

        exception.Message.ShouldContain("steady-state measurement window");
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

    private sealed class ChangingScenario(bool pendingUpload) : IPhysicsChainBenchmarkScenario
    {
        private int _frameCount;

        public int TeardownCount { get; private set; }

        public void Setup(
            in PhysicsChainBenchmarkCase matrixCase,
            PhysicsChainBenchmarkMeasurementKind measurementKind,
            in PhysicsChainBenchmarkDeterministicScenario deterministicScenario)
        {
        }

        public PhysicsChainBenchmarkSettleSnapshot RunFrame()
        {
            ++_frameCount;
            return _frameCount <= 2
                ? new PhysicsChainBenchmarkSettleSnapshot(100, 1L, 0, 0, 0)
                : new PhysicsChainBenchmarkSettleSnapshot(100, pendingUpload ? 1L : 2L, 0, pendingUpload ? 1 : 0, 0);
        }

        public PhysicsChainBenchmarkScenarioMetrics CaptureMetrics() => new();

        public void Teardown() => ++TeardownCount;
    }
}
