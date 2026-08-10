namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IBufferDiagnosticReadbackBackendCapability
{
    bool IBufferDiagnosticReadbackBackendCapability.TryReadBufferBytes(
        XRDataBuffer buffer,
        uint byteOffset,
        Span<byte> destination,
        out string route)
        => TryReadBufferBytesForDiagnostics(buffer, byteOffset, destination, out route);
}
