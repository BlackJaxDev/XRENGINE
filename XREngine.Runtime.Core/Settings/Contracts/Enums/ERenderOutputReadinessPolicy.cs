namespace XREngine;

/// <summary>Controls whether an output may be published before all required resources are ready.</summary>
public enum ERenderOutputReadinessPolicy : byte
{
    /// <summary>Use the normal output fallback policy, including budget-based deferral when allowed.</summary>
    AllowDeferral,

    /// <summary>Wait for the complete fresh output before publishing it.</summary>
    BlockForExact,

    /// <summary>Meet the runtime deadline with an explicitly selected GPU fallback when exact work is not ready.</summary>
    MeetDeadlineWithGpuFallback,
}
