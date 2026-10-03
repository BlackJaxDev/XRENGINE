using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Queue-local diagnostic timestamp selection and optional clock calibration.</summary>
public sealed record RenderProfileGpuConfiguration
{
    [JsonPropertyName("targets")]
    public string[] Targets { get; init; } = [];

    [JsonPropertyName("maximum_scope_depth")]
    public int MaximumScopeDepth { get; init; } = 1;

    [JsonPropertyName("maximum_queries_per_frame")]
    public int MaximumQueriesPerFrame { get; init; } = 128;

    [JsonPropertyName("calibrated_timestamps")]
    public bool CalibratedTimestamps { get; init; }

    [JsonPropertyName("require_calibration")]
    public bool RequireCalibration { get; init; }

    [JsonPropertyName("hardware_counter_indices")]
    public uint[] HardwareCounterIndices { get; init; } = [];

    public void Validate()
    {
        if (Targets is null || Targets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("GPU targets must be non-empty pass names.");
        if (MaximumScopeDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaximumScopeDepth));
        if (MaximumQueriesPerFrame is < 2 or > 1024 || (MaximumQueriesPerFrame & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumQueriesPerFrame), "GPU query capacity must be even and in [2, 1024].");
        if (RequireCalibration && !CalibratedTimestamps)
            throw new ArgumentException("Required calibration must also enable calibrated timestamps.");
        if (HardwareCounterIndices is null || HardwareCounterIndices.Distinct().Count() != HardwareCounterIndices.Length)
            throw new ArgumentException("Hardware counter indices must be unique.");
    }
}
