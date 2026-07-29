namespace XREngine.Rendering.Commands;

/// <summary>
/// Diagnostic IDs for a draw and its material row in the packed GPU tables.
/// </summary>
public readonly record struct AdvancedSharedDrawDependencySnapshot(
    AdvancedDrawDependencySnapshot Scene,
    uint MaterialDenseIndex);
