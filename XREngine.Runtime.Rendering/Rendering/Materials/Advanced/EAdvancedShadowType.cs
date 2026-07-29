namespace XREngine.Rendering;

/// <summary>
/// Projection topology for a native-shading shadow record.
/// </summary>
public enum EAdvancedShadowType : uint
{
    DirectionalCascade = 0,
    Spot = 1,
    PointFace = 2,
    PointCube = 3,
}
