using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Shadow transform, atlas placement, and residency consumed by native shading.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedShadowRecord
{
    public uint StableShadowId;
    public uint Generation;
    public EAdvancedShadowType Type;
    public EAdvancedShadowRecordFlags Flags;

    public AdvancedTextureReference Texture;
    public Matrix4x4 WorldToShadow;
    public Matrix4x4 PreviousWorldToShadow;
    public Vector4 UvScaleBias;
    public Vector4 DepthBiasAndFilter;

    public uint TextureLayer;
    public uint Encoding;
    public uint CascadeOffset;
    public uint CascadeCount;

    public uint ViewMaskLo;
    public uint ViewMaskHi;
    public uint LastRenderedFrameLo;
    public uint LastRenderedFrameHi;
}
