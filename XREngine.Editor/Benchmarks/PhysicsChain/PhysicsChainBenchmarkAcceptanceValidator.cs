namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Rejects incomplete evidence before it can be used for acceptance.</summary>
public static class PhysicsChainBenchmarkAcceptanceValidator
{
    public static void Validate(PhysicsChainBenchmarkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Result.FrameTimesMilliseconds.Length < 1_000
            || evidence.Result.MeasurementDurationSeconds < 30.0)
            throw new InvalidOperationException("Acceptance evidence requires at least 1,000 frames and 30 seconds.");
        if (evidence.CpuHardware.Availability == PhysicsChainBenchmarkMetricAvailability.Unavailable)
            throw new InvalidOperationException("CPU hardware-counter evidence is unavailable.");
        if (evidence.MatrixCase.ExecutionMode is PhysicsChainBenchmarkExecutionMode.GpuStrictZeroReadback
            && evidence.GpuHardware.Availability == PhysicsChainBenchmarkMetricAvailability.Unavailable)
            throw new InvalidOperationException("Strict GPU evidence requires a GPU profiler trace.");
        if (evidence.Arenas.CapacityBytes < evidence.Arenas.LiveBytes)
            throw new InvalidOperationException("Arena live bytes cannot exceed capacity bytes.");
        if (evidence.Arenas.ResourceBreakdownAvailability == PhysicsChainBenchmarkMetricAvailability.Unavailable)
            throw new InvalidOperationException("Per-resource arena evidence is unavailable.");
        ValidateArenaResource("static", evidence.Arenas.Static);
        ValidateArenaResource("state", evidence.Arenas.State);
        ValidateArenaResource("collider", evidence.Arenas.Collider);
        ValidateArenaResource("palette", evidence.Arenas.Palette);
        ValidateArenaResource("bounds", evidence.Arenas.Bounds);
        ValidateArenaResource("readback", evidence.Arenas.Readback);

        if (evidence.Result.CpuReadbackBytes != 0L
            && evidence.MatrixCase.ExecutionMode == PhysicsChainBenchmarkExecutionMode.GpuStrictZeroReadback)
            throw new InvalidOperationException("Strict zero-readback evidence recorded CPU readback bytes.");
    }

    private static void ValidateArenaResource(
        string name,
        PhysicsChainBenchmarkArenaResourceMetrics metrics)
    {
        if (metrics.CapacityBytes < 0L
            || metrics.LiveBytes < 0L
            || metrics.HighWaterBytes < 0L
            || metrics.LiveBytes > metrics.CapacityBytes
            || metrics.HighWaterBytes < metrics.LiveBytes
            || !double.IsFinite(metrics.FragmentationPercent)
            || metrics.FragmentationPercent is < 0.0 or > 100.0)
            throw new InvalidOperationException($"Invalid {name} arena evidence.");
    }
}
