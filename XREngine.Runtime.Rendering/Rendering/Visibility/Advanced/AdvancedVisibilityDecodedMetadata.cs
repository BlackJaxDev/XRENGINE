namespace XREngine.Rendering;

/// <summary>
/// Logical fields decoded from the compact visibility metadata sidecar.
/// </summary>
public readonly record struct AdvancedVisibilityDecodedMetadata(
    EAdvancedGeometryProducer Producer,
    EAdvancedVisibilityRasterOrigin Origin,
    bool Masked,
    bool FrontFace,
    bool VelocityValid,
    uint ViewIndex,
    uint PayloadVersion,
    bool SelectionValid);
