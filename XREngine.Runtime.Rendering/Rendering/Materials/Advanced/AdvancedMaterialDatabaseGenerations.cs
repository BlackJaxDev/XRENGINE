namespace XREngine.Rendering;

/// <summary>
/// Owner-local mutation domains for independently uploadable material tables.
/// </summary>
public readonly record struct AdvancedMaterialDatabaseGenerations(
    AdvancedGpuOwnerGenerations MaterialRows,
    AdvancedGpuOwnerGenerations Kernels,
    AdvancedGpuOwnerGenerations Layouts);
