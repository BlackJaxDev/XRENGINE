using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Generation-checked identities for one material row and the interned schema
/// rows on which it depends.
/// </summary>
public readonly record struct AdvancedMaterialVariantHandles(
    AdvancedGpuHandle Layout,
    AdvancedGpuHandle Kernel,
    AdvancedGpuHandle Material);
