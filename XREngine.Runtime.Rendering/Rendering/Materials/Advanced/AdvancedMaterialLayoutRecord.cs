using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Stable declaration for one packed material layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedMaterialLayoutRecord
{
    public uint StableLayoutId;
    public uint Generation;
    public ulong LayoutHash;

    public uint MemberOffset;
    public uint MemberCount;
    public uint ConstantWordCount;
    public uint TextureReferenceCount;

    public EAdvancedMaterialRequiredAttributeMask RequiredAttributeMask;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
}
