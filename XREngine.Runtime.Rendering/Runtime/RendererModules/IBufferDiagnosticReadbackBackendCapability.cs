namespace XREngine.Rendering;

/// <summary>
/// Provides explicit cold-path buffer readback for diagnostics.
/// </summary>
public interface IBufferDiagnosticReadbackBackendCapability
{
    /// <summary>
    /// Reports the backend allocation size for a buffer when that information is available.
    /// </summary>
    bool TryGetBufferByteSize(XRDataBuffer buffer, out ulong byteSize)
    {
        byteSize = 0;
        return false;
    }

    bool TryReadBufferBytes(
        XRDataBuffer buffer,
        uint byteOffset,
        Span<byte> destination,
        out string route);
}
