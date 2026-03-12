using System;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

[StructLayout(LayoutKind.Sequential)]
public struct GPUTransparencyMetadata
{
    public uint PackedModeAndDomain;
    public uint SortPriority;
    public uint AlphaCutoffBits;
    public uint Flags;

    public ETransparencyMode TransparencyMode
        => (ETransparencyMode)(PackedModeAndDomain & 0xFFFFu);

    public EGpuTransparencyDomain Domain
        => (EGpuTransparencyDomain)((PackedModeAndDomain >> 16) & 0xFFFFu);

    public float AlphaCutoff
        => BitConverter.UInt32BitsToSingle(AlphaCutoffBits);

    public int TransparentSortPriority
        => unchecked((int)SortPriority);

    public static GPUTransparencyMetadata FromMaterial(XRMaterial? material)
    {
        if (material is null)
            return default;

        ETransparencyMode effectiveMode = material.GetEffectiveTransparencyMode();
        EGpuTransparencyDomain domain = GpuTransparencyClassification.ResolveDomain(effectiveMode);

        uint packedModeAndDomain = ((uint)domain << 16) | ((uint)effectiveMode & 0xFFFFu);
        uint alphaCutoffBits = BitConverter.SingleToUInt32Bits(material.AlphaCutoff);
        uint sortPriority = unchecked((uint)material.TransparentSortPriority);

        uint flags = 0u;
        if (GpuTransparencyClassification.IsTransparentLike(effectiveMode))
            flags |= 1u << 0;
        if (domain == EGpuTransparencyDomain.Masked)
            flags |= 1u << 1;
        if (material.RenderOptions?.CullMode == ECullMode.None)
            flags |= 1u << 2;

        return new GPUTransparencyMetadata
        {
            PackedModeAndDomain = packedModeAndDomain,
            SortPriority = sortPriority,
            AlphaCutoffBits = alphaCutoffBits,
            Flags = flags,
        };
    }
}