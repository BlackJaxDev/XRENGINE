using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral shading kernel identity. Material instance identity and
/// mutable constants are intentionally absent.
/// </summary>
public readonly record struct AdvancedShaderCacheKey(
    AdvancedGpuHandle Kernel,
    ulong MaterialLayoutHash,
    uint VertexFormatId,
    EAdvancedMaterialCoverageMode CoverageMode,
    EAdvancedShaderViewMode ViewMode,
    RuntimeGraphicsApiKind Backend,
    EAdvancedTextureIndirectionMode TextureEncoding);
