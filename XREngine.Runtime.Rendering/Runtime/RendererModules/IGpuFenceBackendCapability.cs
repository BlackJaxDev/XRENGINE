namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral synchronization primitives used by stable GPU work dispatchers.
/// </summary>
public interface IGpuFenceBackendCapability
{
    IntPtr CreateCompletionFence();
    bool IsFenceComplete(IntPtr fence);
    void DeleteFence(IntPtr fence);
}
