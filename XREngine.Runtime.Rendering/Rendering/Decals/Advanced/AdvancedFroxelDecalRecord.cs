using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// GPU-written record for froxel decal cell indexing local decals.
/// 8-byte packed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct AdvancedFroxelDecalRecord
{
    public ushort DecalOffset;
    public ushort DecalCount;
    public uint Flags;

    public AdvancedFroxelDecalRecord(ushort offset, ushort count, uint flags = 0u)
    {
        DecalOffset = offset;
        DecalCount = count;
        Flags = flags;
    }
}
