namespace XREngine.Rendering;

/// <summary>
/// Content generations for independently uploadable material database tables.
/// </summary>
public readonly record struct AdvancedMaterialDatabaseGenerations(
    ulong MaterialRows,
    ulong Kernels,
    ulong Layouts);
