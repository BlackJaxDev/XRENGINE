namespace XREngine.Rendering;

/// <summary>
/// One deterministically keyed pass variant prepared for a material.
/// </summary>
public sealed record MaterialPassPrewarmEntry(
    EMaterialPassIdentity Identity,
    ulong VariantKey,
    bool Enabled,
    bool SourcePrepared,
    string? FailureReason = null);
