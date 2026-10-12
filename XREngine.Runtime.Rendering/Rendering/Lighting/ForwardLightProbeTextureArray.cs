using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Runtime probe array with its own canonical filter state; source image sampling remains borrowed.</summary>
[RuntimeOnly]
public sealed class ForwardLightProbeTextureArray : XRTexture2DArray
{
    public ForwardLightProbeTextureArray(XRTexture2D[] sources, bool prefilter) : base(sources)
    {
        SamplingMinFilter = prefilter ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear;
        SamplingMagFilter = ETexMagFilter.Linear;
        CopyGpuLayerSources = true;
        SizedInternalFormat = sources[0].SizedInternalFormat;
        MinLOD = sources[0].MinLOD;
        MaxLOD = sources[0].MaxLOD;
        LargestMipmapLevel = sources[0].LargestMipmapLevel;
        SmallestAllowedMipmapLevel = sources[0].SmallestAllowedMipmapLevel;
    }

    public ETexMinFilter SamplingMinFilter { get; }
    public ETexMagFilter SamplingMagFilter { get; }
}
