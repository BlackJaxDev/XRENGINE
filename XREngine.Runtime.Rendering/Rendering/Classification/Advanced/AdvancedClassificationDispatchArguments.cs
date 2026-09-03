using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU indirect compute dispatch command argument layout matching VkDispatchIndirectCommand.
/// 16-byte packed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct AdvancedClassificationDispatchArguments
{
    public uint WorkGroupCountX;
    public uint WorkGroupCountY;
    public uint WorkGroupCountZ;
    public uint Reserved;

    public AdvancedClassificationDispatchArguments(uint x, uint y = 1u, uint z = 1u)
    {
        WorkGroupCountX = x;
        WorkGroupCountY = y;
        WorkGroupCountZ = z;
        Reserved = 0u;
    }
}
