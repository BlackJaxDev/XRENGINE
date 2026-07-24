namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral shader binary upload totals included in lifecycle diagnostics.
/// </summary>
public readonly record struct ShaderProgramBinaryUploadSummary(
    long CompletedCount,
    long FailedCount,
    long BackpressureCount,
    long CoalescedCount);
