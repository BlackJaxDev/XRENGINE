using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable decal transform and material reference.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedDecalRecord
{
    public AdvancedGpuHandle Identity;
    public AdvancedGpuHandle Material;

    public uint Flags;
    public uint ViewMaskLo;
    public uint ViewMaskHi;
    public uint LayerMask;

    public Matrix4x4 WorldToDecal;
    public Matrix4x4 PreviousWorldToDecal;
    public Vector4 HalfExtentsAndFade;
    public AdvancedTextureReference MaskTexture;
}
