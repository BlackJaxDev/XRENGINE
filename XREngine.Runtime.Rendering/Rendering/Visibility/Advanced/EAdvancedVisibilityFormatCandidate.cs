namespace XREngine.Rendering;

/// <summary>
/// Audited visibility-identity target layouts.
/// </summary>
public enum EAdvancedVisibilityFormatCandidate
{
    TwoR32UIntAttachments = 0,
    OneRg32UIntAttachment,
    PackedR64UIntAttachment,
    NarrowIdentityWithSidecar,
}
