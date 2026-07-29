using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Minimal half-open range changed since the last publication.
/// The units are defined by the owner (records or bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuDirtyRange(uint Start, uint Count)
{
    public static AdvancedGpuDirtyRange Empty => default;

    public bool IsEmpty => Count == 0u;

    public uint EndExclusive => checked(Start + Count);
}
