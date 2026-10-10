using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRenderProgram
{
    /// <summary>Proves that explicit level-zero PBR reads retain the source samplers' complete filtering behavior.</summary>
    internal void RequireUberBaseEnvironmentSampling()
    {
        if (!_samplers.TryGetValue("BRDF", out SamplerSlots brdf) ||
            _resourceOwners[brdf.Texture] is not WebGpuTexture2D { Data: { } image } ||
            image.Mipmaps.Length != 1 || image.AutoGenerateMipmaps || image.MultiSample || image.Rectangle ||
            image.MinFilter != ETexMinFilter.Linear || image.MagFilter != ETexMagFilter.Linear || image.MaxAnisotropy != 1 ||
            image.EnableComparison || image.LodBias != 0 || image.LargestMipmapLevel != 0 || image.MinLOD > 0 || image.MaxLOD < 0)
            throw new NotSupportedException("WebGPU.UberBase.BrdfSamplingUnsupported: the canonical BRDF receiver requires one ordinary linear mip with anisotropy one and no LOD bias.");
        if (!_samplers.TryGetValue("IrradianceArray", out SamplerSlots irradiance) ||
            _resourceOwners[irradiance.Texture] is not WebGpuTexture2DArray { HasFixedLinearBaseMipSampling: true })
            throw new NotSupportedException("WebGPU.UberBase.IrradianceSamplingUnsupported: the canonical irradiance receiver requires non-mip linear sampling with anisotropy one.");
    }

    /// <summary>Publishes the validated prepared Uber block only when its effective bytes changed.</summary>
    internal void PublishUberBaseParameters(ReadOnlySpan<byte> values)
    {
        foreach (WebGpuUniformBlock block in _blocks)
        {
            if (block.Contract.Name != "UberBaseMaterial") continue;
            if (block.Contract.ByteSize != UberBaseParameterSchema.ByteSize || values.Length != UberBaseParameterSchema.ByteSize)
                throw new NotSupportedException("WebGPU.UberBase.ParameterAbiMismatch: the exact 352-byte material parameter ABI is required.");
            if (!values.SequenceEqual(block.Bytes)) values.CopyTo(block.Bytes);
            return;
        }
        throw new NotSupportedException("WebGPU.UberBase.ParameterBlockMissing: the admitted program has no exact Uber material block.");
    }
}
