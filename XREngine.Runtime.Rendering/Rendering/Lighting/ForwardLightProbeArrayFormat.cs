using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Admits a complete exact-format layer set before the shared probe provider allocates an array.</summary>
internal static class ForwardLightProbeArrayFormat
{
    internal static ESizedInternalFormat Resolve(AbstractRenderer renderer, XRTexture2D[] sources, bool prefilter)
    {
        if (renderer.BackendId != RendererBackendId.WebGPU) return ESizedInternalFormat.Rgb16f;
        if (sources.Length == 0) throw Invalid("the array has no retained layers");
        XRTexture2D first = sources[0];
        ESizedInternalFormat format = first.SizedInternalFormat;
        if (format is not (ESizedInternalFormat.Rgba16f or ESizedInternalFormat.Rgba8))
            throw Invalid("each layer requires exact RGBA16F or RGBA8 data; RGB16F array widening is not admitted");
        int levels = first.Mipmaps.Length;
        if (levels < (prefilter ? 5 : 1)) throw Invalid("the complete canonical mip chain is unavailable");
        for (int layer = 0; layer < sources.Length; layer++)
        {
            XRTexture2D source = sources[layer];
            if (source.IsDestroyed || source.MultiSample || source.AutoGenerateMipmaps || source.SizedInternalFormat != format ||
                source.Mipmaps.Length != levels || source.Rectangle || source.LodBias != 0 ||
                source.MinFilter != first.MinFilter || source.MagFilter != first.MagFilter || source.UWrap != first.UWrap || source.VWrap != first.VWrap ||
                source.MinLOD != first.MinLOD || source.MaxLOD != first.MaxLOD || source.LargestMipmapLevel != first.LargestMipmapLevel ||
                source.SmallestAllowedMipmapLevel != first.SmallestAllowedMipmapLevel || source.MaxAnisotropy != first.MaxAnisotropy ||
                source.EnableComparison != first.EnableComparison || source.CompareFunc != first.CompareFunc ||
                source.ImportedColorSpace != first.ImportedColorSpace || source.ImportedUsage != first.ImportedUsage)
                throw Invalid("all retained layers must have identical formats, mip counts, sampling/view state and single-sample storage without rectangle coordinates or LOD bias");
            for (int mip = 0; mip < levels; mip++)
                if (source.Mipmaps[mip].Width != first.Mipmaps[mip].Width || source.Mipmaps[mip].Height != first.Mipmaps[mip].Height ||
                    source.Mipmaps[mip].PixelFormat != first.Mipmaps[mip].PixelFormat || source.Mipmaps[mip].PixelType != first.Mipmaps[mip].PixelType)
                    throw Invalid("all retained layer mips must have identical dimensions and pixel representations");
        }
        return format;
    }

    internal static XRTexture2DArray Create(AbstractRenderer renderer, XRTexture2D[] sources, bool prefilter,
        string name, ESizedInternalFormat format)
    {
        if (renderer.BackendId == RendererBackendId.WebGPU)
            return new ForwardLightProbeTextureArray(sources, prefilter) { Name = name };
        return new XRTexture2DArray(sources)
        {
            Name = name, CopyGpuLayerSources = true,
            MinFilter = prefilter ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear, SizedInternalFormat = format,
        };
    }

    private static NotSupportedException Invalid(string reason) => new($"WebGPU.ProbeArray.SourceUnsupported: {reason}.");
}
