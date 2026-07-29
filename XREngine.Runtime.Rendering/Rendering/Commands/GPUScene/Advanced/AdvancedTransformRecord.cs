using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// One explicitly owned temporal transform row.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedTransformRecord
{
    public Matrix4x4 World;
    public uint FrameSlot;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
}
