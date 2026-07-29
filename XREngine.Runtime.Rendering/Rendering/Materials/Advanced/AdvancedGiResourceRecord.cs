using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral global-illumination resource descriptor.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedGiResourceRecord
{
    public uint StableResourceId;
    public uint Generation;
    public EAdvancedGiResourceType Type;
    public uint Flags;

    public AdvancedTextureReference PrimaryTexture;
    public AdvancedTextureReference SecondaryTexture;
    public AdvancedTextureReference TertiaryTexture;

    public Matrix4x4 WorldToGrid;
    public Vector4 GridOriginAndSpacing;
    public Vector4 GridDimensionsAndMipCount;
    public Vector4 Params0;
    public Vector4 Params1;

    public uint BufferResourceOffset;
    public uint BufferResourceCount;
    public uint ViewMaskLo;
    public uint ViewMaskHi;
}
