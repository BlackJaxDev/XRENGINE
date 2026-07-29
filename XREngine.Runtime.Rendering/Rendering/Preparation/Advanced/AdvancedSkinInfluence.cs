using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Canonical aggregate-skinning influence row. Bone indices address the
/// frame-global precomposed palette and preserve palette row zero as identity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct AdvancedSkinInfluence
{
    public uint Bone0;
    public uint Bone1;
    public uint Bone2;
    public uint Bone3;
    public Vector4 Weights;
    public uint SpillOffset;
    public uint SpillCount;
    public uint Reserved0;
    public uint Reserved1;
}
