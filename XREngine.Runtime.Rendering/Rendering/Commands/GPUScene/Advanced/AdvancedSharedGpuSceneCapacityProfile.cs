namespace XREngine.Rendering.Commands;

/// <summary>
/// Structural capacities for the shared scene and material databases.
/// </summary>
public readonly record struct AdvancedSharedGpuSceneCapacityProfile(
    AdvancedGpuSceneCapacityProfile Scene,
    uint MaterialRecords,
    uint ShadingKernels,
    uint MaterialLayouts,
    uint MaterialLayoutMembers,
    uint MaterialConstantWords,
    uint MaterialTextureBindings,
    uint TextureRecords = 0u,
    uint SamplerRecords = 0u);
