using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainBenchmarkAcceptanceValidatorTests
{
    [Test]
    public void RejectsMissingHardwareCountersAndStrictGpuTrace()
    {
        PhysicsChainBenchmarkEvidence evidence = CreateEvidence();
        Should.Throw<InvalidOperationException>(() => PhysicsChainBenchmarkAcceptanceValidator.Validate(evidence));
    }

    [Test]
    public void AcceptsCompleteLongRunningStrictGpuEvidence()
    {
        PhysicsChainBenchmarkEvidence evidence = CreateEvidence() with
        {
            CpuHardware = new PhysicsChainBenchmarkCpuHardwareMetrics
            {
                Availability = PhysicsChainBenchmarkMetricAvailability.Measured,
                TracePath = "cpu.etl",
            },
            GpuHardware = new PhysicsChainBenchmarkGpuHardwareMetrics
            {
                Availability = PhysicsChainBenchmarkMetricAvailability.Measured,
                TracePath = "gpu.rdc",
            },
            Arenas = new PhysicsChainBenchmarkArenaMetrics
            {
                CapacityBytes = 4_096,
                LiveBytes = 2_048,
                ResourceBreakdownAvailability = PhysicsChainBenchmarkMetricAvailability.Measured,
                Static = ResourceMetrics(1_024, 512),
                State = ResourceMetrics(1_024, 512),
                Collider = ResourceMetrics(512, 256),
                Palette = ResourceMetrics(512, 256),
                Bounds = ResourceMetrics(512, 256),
                Readback = ResourceMetrics(512, 256),

            },
        };

        Should.NotThrow(() => PhysicsChainBenchmarkAcceptanceValidator.Validate(evidence));
    }

    private static PhysicsChainBenchmarkEvidence CreateEvidence()
        => new()
        {

            Result = new PhysicsChainBenchmarkResult
            {
                CompletedAt = DateTimeOffset.UtcNow,
                ScenarioName = "acceptance",
                CopyCount = 10_000,
                DeterministicSeed = 1,
                DebugDisplaysEnabled = false,
                SettleFrameCount = 120,
                MeasurementDurationSeconds = 30.0,
                SpawnMilliseconds = 0.0,
                DestroyMilliseconds = 0.0,
                FrameStatistics = default,
                FrameTimesMilliseconds = new double[1_000],
                CpuUploadBytes = 0,
                GpuCopyBytes = 0,
                CpuReadbackBytes = 0,
                DispatchGroupCount = 1,
                DispatchIterationCount = 1,
                ResidentParticleBytes = 1,
            },
            MatrixCase = new PhysicsChainBenchmarkCase(
                10_000,
                8,
                PhysicsChainBenchmarkTopology.Linear,
                PhysicsChainBenchmarkColliderScenario.None,
                PhysicsChainBenchmarkColliderOwnership.Shared,
                PhysicsChainBenchmarkActivityProfile.Active100,
                PhysicsChainBenchmarkRenderingMode.None,
                PhysicsChainBenchmarkExecutionMode.GpuStrictZeroReadback,
                PhysicsChainBenchmarkReadbackMode.Disabled,
                PhysicsChainBenchmarkRenderBackend.OpenGL,
                60),
            MeasurementKind = PhysicsChainBenchmarkMeasurementKind.SteadyState,
            Environment = null!,
            CpuStages = new PhysicsChainBenchmarkCpuStageMetrics(),
            GpuStages = new PhysicsChainBenchmarkGpuStageMetrics(),
            Population = new PhysicsChainBenchmarkPopulationMetrics(),
            MatchedRunIndex = 0,
            CpuMillisecondsPerActiveChain = 0.0,
            CpuMillisecondsPerActiveParticle = 0.0,
        };

    private static PhysicsChainBenchmarkArenaResourceMetrics ResourceMetrics(long capacity, long live)
        => new()
        {
            CapacityBytes = capacity,
            LiveBytes = live,
            HighWaterBytes = live,
            FragmentationPercent = 0.0,
        };
}
