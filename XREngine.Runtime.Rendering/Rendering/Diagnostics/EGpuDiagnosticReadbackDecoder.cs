namespace XREngine.Rendering.Diagnostics;

/// <summary>Telemetry decoder selected by an immutable diagnostic plan node.</summary>
public enum EGpuDiagnosticReadbackDecoder : byte
{
    None,
    IndirectDrawCount,
    MeshletVisibility,
    SubmissionValidation,
    AdvancedVisibilityCounters,
    AdvancedClassificationCounters,
    AdvancedLightingCounters,
    /// <summary>RVC shadelet telemetry counters; read back only when RVC telemetry is enabled.</summary>
    RvcShadeletTelemetry,
    /// <summary>RVC shadelet cache counters; read back when RVC shadelet shading dispatches.</summary>
    RvcShadeletCache,
}
