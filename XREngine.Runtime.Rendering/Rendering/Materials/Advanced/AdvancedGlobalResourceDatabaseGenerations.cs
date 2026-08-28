namespace XREngine.Rendering;

/// <summary>
/// Owner-local mutation domains for canonical global-resource tables.
/// </summary>
public readonly record struct AdvancedGlobalResourceDatabaseGenerations(
    AdvancedGpuOwnerGenerations Textures,
    AdvancedGpuOwnerGenerations Samplers,
    AdvancedGpuOwnerGenerations Lights,
    AdvancedGpuOwnerGenerations Shadows,
    AdvancedGpuOwnerGenerations Probes,
    AdvancedGpuOwnerGenerations Environments,
    AdvancedGpuOwnerGenerations Decals,
    AdvancedGpuOwnerGenerations GiResources);
