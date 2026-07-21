using System.Runtime.InteropServices;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Backend-neutral three-dimensional compute dispatch command written by the GPU.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PhysicsChainIndirectDispatchArguments
{
    public uint GroupCountX;
    public uint GroupCountY;
    public uint GroupCountZ;
}
