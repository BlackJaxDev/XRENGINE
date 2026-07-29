using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Immutable selection of global tables captured by a compatible command scope.
/// </summary>
public readonly record struct AdvancedGlobalResourceTableSet(
    ulong Generation,
    ulong LayoutHash,
    EAdvancedTextureIndirectionMode TextureEncoding,
    uint Reserved,
    AdvancedGpuHandle Views,
    AdvancedGpuHandle Lights,
    AdvancedGpuHandle Shadows,
    AdvancedGpuHandle Probes,
    AdvancedGpuHandle Environments,
    AdvancedGpuHandle Decals,
    AdvancedGpuHandle GiResources,
    AdvancedGpuHandle Textures,
    AdvancedGpuHandle Samplers,
    AdvancedGpuHandle EncodedTextures,
    AdvancedGpuHandle EncodedSamplers);
