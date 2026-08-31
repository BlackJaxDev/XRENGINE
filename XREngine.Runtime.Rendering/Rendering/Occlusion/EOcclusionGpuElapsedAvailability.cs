namespace XREngine.Rendering.Occlusion;

/// <summary>Availability state for delayed Hi-Z GPU timing telemetry.</summary>
public enum EOcclusionGpuElapsedAvailability : byte
{
    Disabled,
    Pending,
    Ready,
    Unsupported,
    Saturated,
}
