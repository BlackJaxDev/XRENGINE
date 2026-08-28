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
    uint SamplerRecords = 0u,
    uint LightRecords = 0u,
    uint ShadowRecords = 0u,
    uint ProbeRecords = 0u,
    uint EnvironmentRecords = 0u,
    uint DecalRecords = 0u,
    uint GiResourceRecords = 0u);
