using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact logical pair and its publication-retained source; one texture may occupy several sampler-distinct slots.</summary>
internal readonly record struct WebGpuAdvancedTexturePair(AdvancedGpuHandle Texture, AdvancedGpuHandle Sampler,
    EAdvancedTextureDimension Dimension, XRTexture Source, ulong ContentGeneration);
