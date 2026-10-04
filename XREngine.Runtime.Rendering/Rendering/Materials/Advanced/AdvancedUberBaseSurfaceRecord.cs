using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Browser-only 624-byte Uber companion indexed by material stable row identity.
/// Its seven image references own resources independently of canonical material slots.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedUberBaseSurfaceRecord
{
    public const uint CurrentSchemaVersion = 1;
    public const int RoleCount = 7;
    public const uint WordCount = 156;
    public const int ByteSize = 624;

    public uint Generation;
    public uint SchemaVersion;
    public uint Features;
    public uint PipelineFlags;
    public AdvancedUberBaseParameterWords Parameters;
    public AdvancedMaterialTextureBinding MainTexture;
    public AdvancedMaterialTextureBinding NormalTexture;
    public AdvancedMaterialTextureBinding AlphaMask;
    public AdvancedMaterialTextureBinding MetallicTexture;
    public AdvancedMaterialTextureBinding SmoothnessTexture;
    public AdvancedMaterialTextureBinding SpecularTexture;
    public AdvancedMaterialTextureBinding EmissionTexture;
    public uint MainSampling, NormalSampling, AlphaMaskSampling, MetallicSampling;
    public uint SmoothnessSampling, SpecularSampling, EmissionSampling, SamplingReserved;

    /// <summary>Copies exact integer/float bit patterns without interpreting inactive or padding words.</summary>
    public void SetParameters(ReadOnlySpan<byte> values)
    {
        if (values.Length != UberBaseParameterSchema.ByteSize)
            throw new ArgumentException("Uber base parameters require exactly 352 bytes.", nameof(values));
        Span<uint> words = Parameters;
        values.CopyTo(MemoryMarshal.AsBytes(words));
    }

    public readonly uint GetParameterWord(int index) => Parameters[index];

    public readonly AdvancedMaterialTextureBinding GetBinding(int index) => index switch
    {
        0 => MainTexture, 1 => NormalTexture, 2 => AlphaMask, 3 => MetallicTexture,
        4 => SmoothnessTexture, 5 => SpecularTexture, 6 => EmissionTexture,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public readonly uint GetSamplingKey(int index) => index switch
    {
        0 => MainSampling, 1 => NormalSampling, 2 => AlphaMaskSampling, 3 => MetallicSampling,
        4 => SmoothnessSampling, 5 => SpecularSampling, 6 => EmissionSampling,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public void SetSamplingKey(int index, uint value)
    {
        switch (index)
        {
            case 0: MainSampling = value; break;
            case 1: NormalSampling = value; break;
            case 2: AlphaMaskSampling = value; break;
            case 3: MetallicSampling = value; break;
            case 4: SmoothnessSampling = value; break;
            case 5: SpecularSampling = value; break;
            case 6: EmissionSampling = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void SetBindings(ReadOnlySpan<AdvancedMaterialTextureBinding> bindings)
    {
        if (bindings.Length != RoleCount)
            throw new ArgumentException("An Uber base surface requires exactly seven role bindings.", nameof(bindings));
        MainTexture = bindings[0]; NormalTexture = bindings[1]; AlphaMask = bindings[2];
        MetallicTexture = bindings[3]; SmoothnessTexture = bindings[4];
        SpecularTexture = bindings[5]; EmissionTexture = bindings[6];
    }

    public readonly bool HasSameLayout(in AdvancedUberBaseSurfaceRecord other)
    {
        if (SchemaVersion != other.SchemaVersion || Features != other.Features || PipelineFlags != other.PipelineFlags ||
            SamplingReserved != other.SamplingReserved) return false;
        for (int index = 0; index < RoleCount; index++)
            if (GetSamplingKey(index) != other.GetSamplingKey(index)) return false;
        return true;
    }

    public readonly bool HasSameBindings(in AdvancedUberBaseSurfaceRecord other)
    {
        for (int index = 0; index < RoleCount; index++)
            if (GetBinding(index) != other.GetBinding(index)) return false;
        return true;
    }

    /// <summary>Compares the complete numeric image, including integer selectors and signed zero.</summary>
    public readonly bool HasSameContent(in AdvancedUberBaseSurfaceRecord other)
    {
        if (SchemaVersion == 0 && other.SchemaVersion == 0) return true;
        for (int index = 0; index < UberBaseParameterSchema.ByteSize / sizeof(uint); index++)
            if (Parameters[index] != other.Parameters[index]) return false;
        return true;
    }
}
