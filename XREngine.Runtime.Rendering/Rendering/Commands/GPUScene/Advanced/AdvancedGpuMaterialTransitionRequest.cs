using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// One unique desired material variant in a whole-scene ownership transition.
/// The publisher resolves <see cref="MaterialHandle"/> during preflight.
/// </summary>
internal struct AdvancedGpuMaterialTransitionRequest(
    XRMaterial? material,
    MaterialBindingLayout layout,
    EAdvancedMaterialCoverageMode coverage,
    EAdvancedMaterialRenderStateClass state,
    uint constantWordCount,
    uint textureBindingCount,
    uint acquireCount,
    bool requiresPayloadUpdate)
{
    public XRMaterial? Material = material;
    public MaterialBindingLayout Layout = layout;
    public EAdvancedMaterialCoverageMode Coverage = coverage;
    public EAdvancedMaterialRenderStateClass State = state;
    public uint ConstantWordCount = constantWordCount;
    public uint TextureBindingCount = textureBindingCount;
    public uint AcquireCount = acquireCount;
    public bool RequiresPayloadUpdate = requiresPayloadUpdate;
    public AdvancedGpuHandle MaterialHandle = AdvancedGpuHandle.Invalid;
}
