namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Identifies the camera-state input that determined a CPU-query motion tier.
/// This is diagnostic context only; the tier remains the policy decision.
/// </summary>
public enum ECpuOcclusionMotionCause
{
    FirstSample,
    Stable,
    Translation,
    Rotation,
    Projection,
    VrHeadPose,
    InvalidPreviousSnapshot,
    InvalidCurrentSnapshot,
}
