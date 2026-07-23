namespace XREngine.Rendering;

/// <summary>
/// Typed scalar written by an acceleration-structure, micromap, or status provider.
/// </summary>
public readonly record struct RenderQueryPropertyResult(
    ERenderQueryProperty Property,
    ulong Value);
