namespace XREngine.Rendering;

/// <summary>States whether the native queue accepted a fence's command buffer.</summary>
public enum EGpuFenceNativeSubmission
{
    Unknown,
    NotCalled,
    Called,
    Accepted,
}
