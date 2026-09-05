namespace XREngine.Rendering;

/// <summary>
/// Diagnostic visualization modes for native opaque shading and clustered lighting.
/// </summary>
public enum EAdvancedShadingDebugView : uint
{
    Disabled = 0u,
    DirectDiffuse = 1u,
    DirectSpecular = 2u,
    Emissive = 3u,
    FroxelOccupancy = 4u,
    ShadowMask = 5u,
    ReconstructedNormals = 6u,
    Roughness = 7u,
    Metallic = 8u,
    ShadowFallbackReason = 9u,
    AmbientOcclusion = 10u,
}
