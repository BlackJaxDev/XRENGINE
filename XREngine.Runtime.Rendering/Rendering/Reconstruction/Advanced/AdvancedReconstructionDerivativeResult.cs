namespace XREngine.Rendering;

/// <summary>
/// CPU reference result for explicit texture-gradient LOD selection.
/// </summary>
public readonly record struct AdvancedReconstructionDerivativeResult(
    float SelectedMip,
    bool UsesConservativeMip);
