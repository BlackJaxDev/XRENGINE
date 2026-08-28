using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Stable logical sampler metadata.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedSamplerRecord
{
    public uint StableSamplerId;
    public uint Generation;
    public EAdvancedSamplerFilter Filter;
    public EAdvancedSamplerRecordFlags Flags;

    public EAdvancedSamplerAddressMode AddressU;
    public EAdvancedSamplerAddressMode AddressV;
    public EAdvancedSamplerAddressMode AddressW;
    public EAdvancedCompareOperation CompareOperation;

    public Vector4 LodBiasMinMaxAnisotropy;
    public Vector4 BorderColor;
}
