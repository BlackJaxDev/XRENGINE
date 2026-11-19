namespace XREngine.Rendering;

/// <summary>
/// Reports whether all queued and submitted uses of one logical buffer have
/// completed, without waiting or inferring completion from a CPU frame number.
/// </summary>
public interface IGpuBufferContentReuseCapability
{
    EGpuBufferContentReuseStatus QueryBufferContentReuse(XRDataBuffer buffer);

    /// <summary>Captures the current exact native allocation for a GPU buffer.</summary>
    bool TryCaptureBufferAllocationIdentity(
        XRDataBuffer buffer,
        out GpuBufferAllocationIdentity identity)
    {
        identity = default;
        return false;
    }
}
