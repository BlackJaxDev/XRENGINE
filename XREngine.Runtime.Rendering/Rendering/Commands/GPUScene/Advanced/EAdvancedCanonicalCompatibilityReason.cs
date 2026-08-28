namespace XREngine.Rendering.Commands;

/// <summary>
/// Exact reason a legacy draw remains outside the canonical resident stream.
/// These are compatibility outcomes, not whole-publication failures.
/// </summary>
public enum EAdvancedCanonicalCompatibilityReason : uint
{
    None = 0,
    MissingCanonicalSource = 1,
    UnsupportedRenderPass = 2,
    LegacyStateMismatch = 3,
    UnsupportedResourceBinding = 4,
    UnsupportedResourceTextureType = 5,
    UnsupportedResourceTextureShape = 6,
    EmptyResourceTexture = 7,
    NonFiniteResourceSampler = 8,
    UnsupportedResourceTextureFormat = 9,
    UnsupportedResourceSamplerAddressMode = 10,
    UnsupportedResourceSamplerCompareOperation = 11,
    ResourceComparisonRequiresDepth = 12,
    UnsupportedGeometryTopology = 13,
    InvalidGeometrySource = 14,
}
