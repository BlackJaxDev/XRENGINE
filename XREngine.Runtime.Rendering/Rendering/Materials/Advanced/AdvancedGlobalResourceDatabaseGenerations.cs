namespace XREngine.Rendering;

/// <summary>
/// Content generations for the canonical texture and sampler tables.
/// </summary>
public readonly record struct AdvancedGlobalResourceDatabaseGenerations(
    ulong Textures,
    ulong Samplers);
