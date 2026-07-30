namespace XREngine.Rendering;

/// <summary>
/// Backend- and producer-neutral surface identity used by reconstruction, picking, and tests.
/// </summary>
public readonly record struct AdvancedVisibilityLogicalSurface(
    uint DrawTableIndex,
    EAdvancedGeometryProducer Producer,
    AdvancedVisibilityDecodedPrimitive Primitive,
    uint SelectionId,
    uint ViewIndex);
