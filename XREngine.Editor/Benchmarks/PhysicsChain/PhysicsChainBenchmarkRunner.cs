using System.Buffers;
using System.Diagnostics;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Focused benchmark runner that enforces preflight, settle/warmup exclusion,
/// bounded sample storage, exact whole-frame timing, and separate teardown.
/// </summary>
public sealed class PhysicsChainBenchmarkRunner
{
    private readonly PhysicsChainBenchmarkConfiguration _configuration;
    private readonly int _maximumSampleCount;

    public PhysicsChainBenchmarkRunner(
        PhysicsChainBenchmarkConfiguration configuration,
        int maximumSampleCount = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSampleCount, configuration.MinimumSampleFrameCount);
        _configuration = configuration;
        _maximumSampleCount = maximumSampleCount;
    }

    public PhysicsChainBenchmarkEvidence Run(
        IPhysicsChainBenchmarkScenario scenario,
        in PhysicsChainBenchmarkWorkItem workItem,
        PhysicsChainBenchmarkRunPolicy runPolicy,
        PhysicsChainBenchmarkEnvironment environment,
        in PhysicsChainBenchmarkProcessState processState)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        PhysicsChainBenchmarkPreflight.Validate(
            runPolicy,
            environment,
            processState.DebuggerAttached,
            processState.ValidationLayersEnabled,
            processState.VerbosePerChainLoggingEnabled,
            processState.DebugDrawingEnabled,
            processState.EditorOnlyInstrumentationEnabled);

        double[] samples = ArrayPool<double>.Shared.Rent(_maximumSampleCount);
        bool setupAttempted = false;
        bool teardownAttempted = false;
        try
        {
            int sampleCount = 0;
            long setupStart = Stopwatch.GetTimestamp();
            setupAttempted = true;
            var deterministicScenario = new PhysicsChainBenchmarkDeterministicScenario(
                workItem.MatrixCase,
                _configuration.DeterministicSeed);
            scenario.Setup(
                workItem.MatrixCase, workItem.MeasurementKind, deterministicScenario);
            double setupMilliseconds = Stopwatch.GetElapsedTime(setupStart).TotalMilliseconds;

            var controller = new PhysicsChainBenchmarkRunController(_configuration);
            PhysicsChainBenchmarkSettleSnapshot settledSnapshot = default;
            while (controller.State == PhysicsChainBenchmarkRunState.Settling)
            {
                settledSnapshot = scenario.RunFrame();
                controller.ObserveSettleFrame(settledSnapshot);
            }
            if (controller.State == PhysicsChainBenchmarkRunState.SettleTimedOut)
                throw new InvalidOperationException("Physics-chain benchmark scenario did not settle before the configured limit.");

            double measurementSeconds = 0.0;
            long measurementStart = Stopwatch.GetTimestamp();
            while (controller.State == PhysicsChainBenchmarkRunState.Measuring)
            {
                if (sampleCount == _maximumSampleCount)
                    throw new InvalidOperationException("Physics-chain benchmark sample capacity was exhausted.");
                long frameStart = Stopwatch.GetTimestamp();
                PhysicsChainBenchmarkSettleSnapshot measurementSnapshot = scenario.RunFrame();
                if (measurementSnapshot.HasPendingWork || measurementSnapshot != settledSnapshot)
                    throw new InvalidOperationException(
                        "Physics-chain resources or pending work changed during the steady-state measurement window.");
                samples[sampleCount++] = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                measurementSeconds = Stopwatch.GetElapsedTime(measurementStart).TotalSeconds;
                controller.ObserveMeasurement(measurementSeconds, sampleCount);
            }

            PhysicsChainBenchmarkScenarioMetrics metrics = scenario.CaptureMetrics();
            long teardownStart = Stopwatch.GetTimestamp();
            teardownAttempted = true;
            scenario.Teardown();
            double teardownMilliseconds = Stopwatch.GetElapsedTime(teardownStart).TotalMilliseconds;

            double[] retainedSamples = samples.AsSpan(0, sampleCount).ToArray();
            PhysicsChainBenchmarkFrameStatistics statistics = PhysicsChainBenchmarkFrameStatistics.Calculate(retainedSamples);
            double[] gpuSamples = metrics.GpuFrameTimesMilliseconds;
            PhysicsChainBenchmarkFrameStatistics? gpuStatistics = gpuSamples.Length == 0
                ? null
                : PhysicsChainBenchmarkFrameStatistics.Calculate(gpuSamples);
            int activeChains = Math.Max(metrics.Population.ActiveChains, 1);
            int activeParticles = Math.Max(metrics.Population.ActiveParticles, 1);
            var result = new PhysicsChainBenchmarkResult
            {
                CompletedAt = DateTimeOffset.UtcNow,
                ScenarioName = workItem.MatrixCase.ToString(),
                CopyCount = workItem.MatrixCase.ChainCount,
                DeterministicSeed = _configuration.DeterministicSeed,
                DebugDisplaysEnabled = processState.DebugDrawingEnabled,
                SettleFrameCount = controller.SettleFrameCount,
                MeasurementDurationSeconds = measurementSeconds,
                SpawnMilliseconds = setupMilliseconds,
                DestroyMilliseconds = teardownMilliseconds,
                FrameStatistics = statistics,
                FrameTimesMilliseconds = retainedSamples,
                CpuUploadBytes = metrics.CpuUploadBytes,
                GpuFrameTimesMilliseconds = gpuSamples,
                GpuFrameStatistics = gpuStatistics,
                GpuCopyBytes = metrics.GpuCopyBytes,
                CpuReadbackBytes = metrics.CpuReadbackBytes,
                DispatchGroupCount = metrics.DispatchGroupCount,
                DispatchIterationCount = metrics.DispatchIterationCount,
                ResidentParticleBytes = metrics.ResidentParticleBytes,
            };
            return new PhysicsChainBenchmarkEvidence
            {
                Result = result,
                MatrixCase = workItem.MatrixCase,
                MeasurementKind = workItem.MeasurementKind,
                Environment = environment,
                CpuStages = metrics.CpuStages,
                GpuStages = metrics.GpuStages,
                Population = metrics.Population,
                MatchedRunIndex = workItem.MatchedRunIndex,
                CpuMillisecondsPerActiveChain = statistics.MeanMilliseconds / activeChains,
                CpuMillisecondsPerActiveParticle = statistics.MeanMilliseconds / activeParticles,
                CpuHardware = metrics.CpuHardware,
                GpuHardware = metrics.GpuHardware,
                Arenas = metrics.Arenas,
            };
        }
        finally
        {
            if (setupAttempted && !teardownAttempted)
                scenario.Teardown();
            ArrayPool<double>.Shared.Return(samples, clearArray: false);
        }
    }
}
