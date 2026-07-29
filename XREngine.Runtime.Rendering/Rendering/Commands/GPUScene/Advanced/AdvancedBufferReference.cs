using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Backend-neutral immutable buffer slice. Backends may encode
/// <see cref="Buffer"/> as a descriptor row, binding-table row, or device address.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedBufferReference(
    AdvancedGpuHandle Buffer,
    ulong ByteOffset,
    uint ElementOffset,
    uint ElementCount,
    uint ElementStride,
    uint Flags)
{
    public static AdvancedBufferReference Invalid => default;

    public bool IsValid
        => Buffer.IsValid && ElementCount != 0u && ElementStride != 0u;

    public ulong ByteLength => (ulong)ElementCount * ElementStride;
}
