using System.Runtime.InteropServices;

namespace XREngine.Scene.Components.Particles;

/// <summary>
/// Indirect draw command structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawIndirectCommand
{
    public uint VertexCount;
    public uint InstanceCount;
    public uint FirstVertex;
    public uint FirstInstance;
}
