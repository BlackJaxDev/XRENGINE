using System.Text.Json.Serialization;

namespace XREngine.Rendering.Profiling;

/// <summary>Optional per-run acceptance limits, evaluated after query drainage.</summary>
public sealed record RenderProfileAcceptanceBudgets
{
    [JsonPropertyName("max_cpu_p50_ms")]
    public double? MaxCpuP50Milliseconds { get; init; }

    [JsonPropertyName("max_cpu_p95_ms")]
    public double? MaxCpuP95Milliseconds { get; init; }

    [JsonPropertyName("max_gpu_p95_ms")]
    public double? MaxGpuP95Milliseconds { get; init; }

    [JsonPropertyName("max_capture_thread_allocated_bytes")]
    public long? MaxCaptureThreadAllocatedBytes { get; init; } = 0;

    [JsonPropertyName("max_worker_allocated_bytes")]
    public long? MaxWorkerAllocatedBytes { get; init; } = 0;

    [JsonPropertyName("require_output_hash")]
    public bool RequireOutputHash { get; init; } = true;

    [JsonPropertyName("required_output_sha256")]
    public string? RequiredOutputSha256 { get; init; }

    public void Validate()
    {
        if (FiniteNonNegative(MaxCpuP50Milliseconds) is false || FiniteNonNegative(MaxCpuP95Milliseconds) is false ||
            FiniteNonNegative(MaxGpuP95Milliseconds) is false || MaxCaptureThreadAllocatedBytes < 0 || MaxWorkerAllocatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCpuP50Milliseconds), "Acceptance budgets must be finite and non-negative.");
        if (RequiredOutputSha256 is not null && (RequiredOutputSha256.Length != 64 || RequiredOutputSha256.Any(static character => !Uri.IsHexDigit(character))))
            throw new ArgumentException("required_output_sha256 must be a 64-character SHA-256 value.");
    }

    private static bool? FiniteNonNegative(double? value)
        => !value.HasValue ? null : double.IsFinite(value.Value) && value.Value >= 0.0;
}
