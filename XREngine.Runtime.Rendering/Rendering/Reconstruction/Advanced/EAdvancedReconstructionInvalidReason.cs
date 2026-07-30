namespace XREngine.Rendering;

/// <summary>
/// Defined failure returned instead of reading arbitrary reconstruction storage.
/// </summary>
public enum EAdvancedReconstructionInvalidReason : uint
{
    None = 0u,
    BackgroundOrInvalidPayload,
    PayloadVersion,
    DrawNotResident,
    StaleDependencyGeneration,
    GeometryMissing,
    GeometryNonResident,
    PrimitiveOutOfRange,
    MaterialNotResident,
    ShadingKernelNotResident,
    ViewOutOfRange,
    DegenerateTriangle,
    NonFiniteAttribute,
    MissingCurrentGeometry,
    MissingPreviousGeometry,
}
