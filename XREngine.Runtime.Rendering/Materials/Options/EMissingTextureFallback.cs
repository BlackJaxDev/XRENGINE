namespace XREngine.Rendering.Models.Materials;

/// <summary>
/// Selects the visible value used when a sampled-texture slot is intentionally unassigned.
/// This policy does not apply to an assigned resource that is still uploading or otherwise unavailable.
/// </summary>
public enum EMissingTextureFallback
{
    /// <summary>Uses a conspicuous magenta placeholder so unintended omissions are easy to diagnose.</summary>
    DiagnosticMagenta = 0,

    /// <summary>Uses an opaque black placeholder for content where an empty slot is a valid neutral value.</summary>
    Black = 1,
}
