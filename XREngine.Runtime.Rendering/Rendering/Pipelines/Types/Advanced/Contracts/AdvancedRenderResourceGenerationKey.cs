namespace XREngine.Rendering;

/// <summary>
/// Structural key for one immutable advanced resource/state profile.
/// Equality deliberately compares every profile field instead of a lossy hash.
/// </summary>
public readonly record struct AdvancedRenderResourceGenerationKey(
    AdvancedRenderResourceProfile Profile);
