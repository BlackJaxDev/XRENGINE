namespace XREngine.Rendering;

/// <summary>
/// Frozen phase-04 format decision. The metadata and optional selection words
/// are sidecars because their lifetimes and consumers differ from identity.
/// </summary>
public static class AdvancedVisibilityFormatDecision
{
    private static readonly AdvancedVisibilityFormatCandidate[] Candidates =
    [
        new(
            EAdvancedVisibilityFormatCandidate.TwoR32UIntAttachments,
            8u,
            2u,
            CoreOpenGl46: true,
            CoreVulkan: true,
            PreservesFullDrawAndPrimitiveRange: true,
            "Portable but consumes an extra color attachment and output location."),
        new(
            EAdvancedVisibilityFormatCandidate.OneRg32UIntAttachment,
            8u,
            1u,
            CoreOpenGl46: true,
            CoreVulkan: true,
            PreservesFullDrawAndPrimitiveRange: true,
            "Selected: identical bandwidth with one attachment and one image transition."),
        new(
            EAdvancedVisibilityFormatCandidate.PackedR64UIntAttachment,
            8u,
            1u,
            CoreOpenGl46: false,
            CoreVulkan: true,
            PreservesFullDrawAndPrimitiveRange: true,
            "Rejected because portable 64-bit integer color-attachment support is not core OpenGL 4.6."),
        new(
            EAdvancedVisibilityFormatCandidate.NarrowIdentityWithSidecar,
            6u,
            2u,
            CoreOpenGl46: true,
            CoreVulkan: true,
            PreservesFullDrawAndPrimitiveRange: false,
            "Rejected because target primitive and editor ranges would overflow or require frame-dependent remapping."),
    ];

    public static EAdvancedVisibilityFormatCandidate Selected
        => EAdvancedVisibilityFormatCandidate.OneRg32UIntAttachment;

    public static ReadOnlySpan<AdvancedVisibilityFormatCandidate> Inventory
        => Candidates;
}
