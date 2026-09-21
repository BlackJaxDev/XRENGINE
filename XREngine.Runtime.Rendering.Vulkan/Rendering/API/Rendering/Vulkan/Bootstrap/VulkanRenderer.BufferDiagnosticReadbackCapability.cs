namespace XREngine.Rendering.Vulkan;

/// <summary>Provides explicit, accepted-frame diagnostic buffer observation for cold-path consumers.</summary>
public sealed partial class VulkanRenderer : IBufferDiagnosticReadbackBackendCapability
{
    bool IBufferDiagnosticReadbackBackendCapability.TryGetBufferByteSize(
        XRDataBuffer buffer,
        out ulong byteSize)
    {
        if (_resourceRuntime.WrapperLookup.GetOrCreate(buffer, generateNow: false) is VkDataBuffer
            {
                IsGenerated: true,
                BackendAllocatedByteSize: var allocatedByteSize,
            })
        {
            byteSize = allocatedByteSize;
            return allocatedByteSize != 0;
        }

        byteSize = 0;
        return false;
    }

    bool IBufferDiagnosticReadbackBackendCapability.TryReadBufferBytes(
        XRDataBuffer buffer,
        uint byteOffset,
        Span<byte> destination,
        out string route)
        => _frameLoop.TryReadAcceptedPipelineBufferBytesForDiagnostics(
            buffer,
            byteOffset,
            destination,
            out route);
}
