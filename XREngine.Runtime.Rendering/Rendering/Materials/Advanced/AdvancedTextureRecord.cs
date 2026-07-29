using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable logical texture metadata. Backend descriptors live in the encoded
/// resource table rather than this record.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedTextureRecord
{
    public uint StableTextureId;
    public uint Generation;
    public EAdvancedTextureDimension Dimension;
    public EAdvancedTextureRecordFlags Flags;

    public uint Width;
    public uint Height;
    public uint DepthOrLayers;
    public uint MipCount;

    public uint FormatClass;
    public uint EncodedReferenceIndex;
    public AdvancedGpuHandle DefaultSampler;

    public Vector4 UvScaleBias;
}
