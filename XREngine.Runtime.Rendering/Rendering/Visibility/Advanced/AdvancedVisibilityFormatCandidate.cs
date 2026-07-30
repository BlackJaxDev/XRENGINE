namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral bandwidth and portability comparison for one identity pixel.
/// </summary>
public readonly record struct AdvancedVisibilityFormatCandidate(
    EAdvancedVisibilityFormatCandidate Candidate,
    uint IdentityBytesPerPixel,
    uint AttachmentCount,
    bool CoreOpenGl46,
    bool CoreVulkan,
    bool PreservesFullDrawAndPrimitiveRange,
    string Tradeoff);
