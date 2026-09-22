namespace XREngine.Rendering.Vulkan;

internal enum EVulkanPrimaryCommandRecordingDisposition : byte
{
    Recorded,
    /// <summary>Recorded using an explicitly selected GPU fallback for a deadline-bound output.</summary>
    RecordedWithGpuFallback,
    Reused,
    ReplanRequired,
    Deferred,
    /// <summary>No desktop scene producer authored this tick; only recovery/UI may present.</summary>
    NoAuthoredOutput,
    /// <summary>Recording could not satisfy a present-now output and must not reuse old content.</summary>
    Failed,
}
