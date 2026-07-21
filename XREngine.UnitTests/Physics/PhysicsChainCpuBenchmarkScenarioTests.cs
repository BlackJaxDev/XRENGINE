using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Benchmarks.PhysicsChain;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainCpuBenchmarkScenarioTests
{
    [TestCase(PhysicsChainBenchmarkTopology.Linear, PhysicsChainBenchmarkExecutionMode.CpuStrict)]
    [TestCase(PhysicsChainBenchmarkTopology.Branched, PhysicsChainBenchmarkExecutionMode.CpuQualityTiered)]
    public void RuntimeScenario_UsesDeterministicPopulationAndAllocatesNothingPerWarmFrame(
        PhysicsChainBenchmarkTopology topology,
        PhysicsChainBenchmarkExecutionMode executionMode)
    {
        PhysicsChainBenchmarkCase matrixCase = CreateCase(
            topology,
            PhysicsChainBenchmarkColliderOwnership.Shared,
            executionMode,
            PhysicsChainBenchmarkActivityProfile.Active50);
        var deterministic = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 1234);
        var scenario = new PhysicsChainCpuBenchmarkScenario();
        scenario.Setup(matrixCase, PhysicsChainBenchmarkMeasurementKind.SteadyState, deterministic);
        for (int i = 0; i < 8; ++i)
            scenario.RunFrame();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; ++i)
            scenario.RunFrame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        PhysicsChainBenchmarkScenarioMetrics metrics = scenario.CaptureMetrics();
        int expectedActive = 0;
        for (int chainIndex = 0; chainIndex < matrixCase.ChainCount; ++chainIndex)
            if (deterministic.Sample(chainIndex, 108L).IsActive)
                ++expectedActive;
        allocated.ShouldBe(0L);
        metrics.Population.ActiveChains.ShouldBe(expectedActive);
        metrics.Population.SleepingChains.ShouldBe(matrixCase.ChainCount - expectedActive);
        metrics.Population.ActiveParticles.ShouldBe(expectedActive * matrixCase.DynamicSegmentCount);
        metrics.Arenas.InstanceLiveCount.ShouldBe(matrixCase.ChainCount);
        metrics.Arenas.ParticleLiveCount.ShouldBe(matrixCase.ChainCount * matrixCase.DynamicSegmentCount);
        metrics.Arenas.ResourceBreakdownAvailability.ShouldBe(PhysicsChainBenchmarkMetricAvailability.Measured);
        metrics.GpuFrameTimesMilliseconds.ShouldBeEmpty();
        metrics.CpuReadbackBytes.ShouldBe(0L);
        scenario.Teardown();
    }

    [Test]
    public void RuntimeScenario_AccountsSharedAndUniqueColliderOwnershipSeparately()
    {
        PhysicsChainBenchmarkCase sharedCase = CreateCase(
            PhysicsChainBenchmarkTopology.Branched,
            PhysicsChainBenchmarkColliderOwnership.Shared,
            PhysicsChainBenchmarkExecutionMode.CpuStrict,
            PhysicsChainBenchmarkActivityProfile.Active100);
        PhysicsChainBenchmarkCase uniqueCase = sharedCase with
        {
            ColliderOwnership = PhysicsChainBenchmarkColliderOwnership.Unique,
        };

        long sharedBytes = RunAndCaptureColliderBytes(sharedCase);
        long uniqueBytes = RunAndCaptureColliderBytes(uniqueCase);

        uniqueBytes.ShouldBe(sharedBytes * sharedCase.ChainCount);
    }

    [Test]
    public void RuntimeScenario_RejectsGpuAndLiveRendererBuckets()
    {
        PhysicsChainBenchmarkCase cpuCase = CreateCase(
            PhysicsChainBenchmarkTopology.Linear,
            PhysicsChainBenchmarkColliderOwnership.Shared,
            PhysicsChainBenchmarkExecutionMode.CpuStrict,
            PhysicsChainBenchmarkActivityProfile.Active100);
        var scenario = new PhysicsChainCpuBenchmarkScenario();

        PhysicsChainBenchmarkCase gpuCase = cpuCase with
        {
            ExecutionMode = PhysicsChainBenchmarkExecutionMode.GpuStrictZeroReadback,
        };
        Should.Throw<NotSupportedException>(() =>
            scenario.Setup(gpuCase, PhysicsChainBenchmarkMeasurementKind.SteadyState,
                new PhysicsChainBenchmarkDeterministicScenario(gpuCase, 1)));

        PhysicsChainBenchmarkCase renderedCase = cpuCase with
        {
            RenderingMode = PhysicsChainBenchmarkRenderingMode.IdenticalInstancedMeshes,
        };
        Should.Throw<NotSupportedException>(() =>
            scenario.Setup(renderedCase, PhysicsChainBenchmarkMeasurementKind.SteadyState,
                new PhysicsChainBenchmarkDeterministicScenario(renderedCase, 1)));
    }

    private static long RunAndCaptureColliderBytes(PhysicsChainBenchmarkCase matrixCase)
    {
        var deterministic = new PhysicsChainBenchmarkDeterministicScenario(matrixCase, 5678);
        var scenario = new PhysicsChainCpuBenchmarkScenario();
        scenario.Setup(matrixCase, PhysicsChainBenchmarkMeasurementKind.SteadyState, deterministic);
        scenario.RunFrame();
        long bytes = scenario.CaptureMetrics().Arenas.Collider.LiveBytes;
        scenario.Teardown();
        return bytes;
    }

    private static PhysicsChainBenchmarkCase CreateCase(
        PhysicsChainBenchmarkTopology topology,
        PhysicsChainBenchmarkColliderOwnership ownership,
        PhysicsChainBenchmarkExecutionMode executionMode,
        PhysicsChainBenchmarkActivityProfile activity)
        => new(
            ChainCount: 16,
            DynamicSegmentCount: 8,
            topology,
            ColliderScenario: PhysicsChainBenchmarkColliderScenario.FiveMixed,
            ownership,
            activity,
            RenderingMode: PhysicsChainBenchmarkRenderingMode.PaletteAndBounds,
            executionMode,
            ReadbackMode: PhysicsChainBenchmarkReadbackMode.DiagnosticFullSync,
            RenderBackend: PhysicsChainBenchmarkRenderBackend.OpenGL,
            FixedSimulationRateHz: 60);
}
