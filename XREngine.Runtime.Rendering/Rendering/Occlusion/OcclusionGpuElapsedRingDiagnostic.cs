namespace XREngine.Rendering.Occlusion;

/// <summary>Read-only state of the bounded Hi-Z GPU timestamp ring.</summary>
public readonly record struct OcclusionGpuElapsedRingDiagnostic(
    int Capacity,
    int Available,
    int Open,
    int Pending,
    int Quarantined,
    int StartReady,
    int EndReady,
    int StartAbandoned,
    int EndAbandoned);
