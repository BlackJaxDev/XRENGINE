using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Exact sampled role metadata retained independently of the legacy material texture layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct AdvancedEngineSurfaceTextureRole
{
    public AdvancedMaterialTextureBinding Binding;
    public Vector4 UvScaleOffset;
    public float UvRotation;
    public uint TexCoordSet;
    public uint Channel;
    public uint DecodeFlags;

    public readonly bool HasSameSamplingMetadata(in AdvancedEngineSurfaceTextureRole other)
        => UvScaleOffset == other.UvScaleOffset && UvRotation == other.UvRotation &&
            TexCoordSet == other.TexCoordSet && Channel == other.Channel && DecodeFlags == other.DecodeFlags;
}
