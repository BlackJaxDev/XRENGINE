using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Stable mapping from a tree candidate to its owning instance and solver bucket.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsChainGpuTreeWorkItem
{
    public uint InstanceIndex;
    public uint TreeParamIndex;
    public uint KernelBucket;
    public uint TopologyDepth;
}
