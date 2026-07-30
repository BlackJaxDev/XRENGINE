namespace XREngine.Rendering;

/// <summary>
/// Shader-visible validity and temporal properties of one reconstructed pixel.
/// </summary>
[Flags]
public enum EAdvancedReconstructionSurfaceFlags : uint
{
    None = 0u,
    Valid = 1u << 0,
    BackFacing = 1u << 1,
    FlatAttributes = 1u << 2,
    Deformed = 1u << 3,
    DerivativesValid = 1u << 4,
    VelocityValid = 1u << 5,
    Reactive = 1u << 6,
    MaskedEdge = 1u << 7,
    MirroredTransform = 1u << 8,
    ConservativeMip = 1u << 9,
}
