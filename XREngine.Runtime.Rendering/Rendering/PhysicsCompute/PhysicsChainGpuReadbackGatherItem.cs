using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// GPU-readable packed gather command. Source indices are absolute arena indices
/// and destination offsets are expressed in 32-bit words.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PhysicsChainGpuReadbackGatherItem
{
    public uint Kind;
    public uint SourceIndex;
    public uint DestinationWordOffset;
    public uint WordCount;
}
