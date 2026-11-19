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

    /// <summary>
    /// Diagnostics only: a one-line description of why <paramref name="buffer"/> is or is not
    /// reusable (for example the completion frontier that still holds it), or null when the
    /// backend has none. Allocates.
    /// </summary>
    string? DescribeBufferContentReuse(XRDataBuffer buffer) => null;
}
