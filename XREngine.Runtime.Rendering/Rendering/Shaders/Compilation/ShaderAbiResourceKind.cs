namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Vulkan descriptor shape permitted by the explicit shader ABI.
/// </summary>
public enum ShaderAbiResourceKind
{
    UniformBuffer,
    StorageBuffer,
    CombinedImageSampler,
    SampledImage,
    Sampler,
    StorageImage,
}
