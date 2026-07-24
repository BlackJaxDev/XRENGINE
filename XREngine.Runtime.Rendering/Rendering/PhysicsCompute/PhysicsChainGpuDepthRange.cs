using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Contiguous particle-ID range for one dependency depth of one GPU physics-chain tree.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsChainGpuDepthRange
{
    public uint ParticleIdOffset;
    public uint ParticleCount;
}
