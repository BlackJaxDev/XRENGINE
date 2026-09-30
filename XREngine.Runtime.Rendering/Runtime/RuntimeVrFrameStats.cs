namespace XREngine;

/// <summary>Aggregated OpenVR compositor timing since the prior sample.</summary>
public readonly record struct RuntimeVrFrameStats(
    uint LastFrameSampleIndex,
    float GpuFrameTimeMs,
    float CpuFrameTimeMs,
    float TotalFrameTimeMs,
    float FrameRate);
