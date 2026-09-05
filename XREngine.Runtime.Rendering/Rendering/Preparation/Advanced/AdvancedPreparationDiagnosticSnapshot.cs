namespace XREngine.Rendering;

/// <summary>Coherent, on-demand diagnostics for shared preparation and its last admission decision.</summary>
public readonly record struct AdvancedPreparationDiagnosticSnapshot(
    AdvancedPreparationPublication Publication,
    string DeferralReason,
    string OutputReuseStatus,
    uint OutputReuseSlot,
    string OutputReuseAuthority);
