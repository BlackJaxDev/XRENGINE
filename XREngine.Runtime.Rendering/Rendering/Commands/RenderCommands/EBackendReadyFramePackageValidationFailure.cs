namespace XREngine.Rendering.Commands;

/// <summary>
/// Countable reasons a published package cannot be consumed.
/// </summary>
public enum EBackendReadyFramePackageValidationFailure
{
    None = 0,
    NotPublished = 1,
    CollectGenerationMismatch = 2,
    CommandGenerationMismatch = 3,
    ResourceGenerationMismatch = 4,
    DescriptorGenerationMismatch = 5,
    RenderGraphGenerationMismatch = 6,
    ViewportMismatch = 7,
}
