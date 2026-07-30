namespace XREngine.Rendering;

/// <summary>
/// Vertex attributes required to reconstruct and shade a material.
/// </summary>
[Flags]
public enum EAdvancedMaterialRequiredAttributeMask : uint
{
    None = 0,
    Position = 1u << 0,
    Normal = 1u << 1,
    Tangent = 1u << 2,
    TexCoord0 = 1u << 3,
    TexCoord1 = 1u << 4,
    Color0 = 1u << 5,
    JointIndices = 1u << 6,
    JointWeights = 1u << 7,
    Custom0 = 1u << 8,
    FlatAttributes = 1u << 9,
    DeformedPosition = 1u << 10,
    AnalyticalDerivatives = 1u << 11,
    Color1 = 1u << 12,
}
