namespace XREngine;

public readonly record struct RvcFrameCounters(
    RvcVisibilityCounters Visibility,
    RvcShadeletTelemetryCounters Shadelets,
    RvcSharedLightingCounters SharedLighting,
    RvcReuseCounters Reuse,
    RvcTemporalCounters Temporal)
{
    public static RvcFrameCounters Empty => default;
}
