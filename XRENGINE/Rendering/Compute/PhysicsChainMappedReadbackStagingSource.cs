using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Lease over a persistently mapped staging buffer. The dispatcher retains the
/// buffer; disposing this object only ends the service slot's lease.
/// </summary>
internal sealed class PhysicsChainMappedReadbackStagingSource(
    XRDataBuffer buffer,
    int byteCount) : IPhysicsChainReadbackStagingSource
{
    private VoidPtr _mappedAddress = ResolveMappedAddress(buffer);

    public int ByteCount => _mappedAddress.IsValid ? byteCount : 0;
    public bool IsValid => _mappedAddress.IsValid;

    public unsafe bool TryCopyTo(Span<byte> destination)
    {
        if (!_mappedAddress.IsValid || destination.Length != byteCount)
            return false;

        fixed (byte* destinationPointer = destination)
            Memory.Move(destinationPointer, _mappedAddress.Pointer, (uint)byteCount);
        return true;
    }

    public void Dispose()
        => _mappedAddress = VoidPtr.Zero;

    private static VoidPtr ResolveMappedAddress(XRDataBuffer buffer)
    {
        foreach (VoidPtr address in buffer.GetMappedAddresses())
            if (address.IsValid)
                return address;

        return VoidPtr.Zero;
    }
}
