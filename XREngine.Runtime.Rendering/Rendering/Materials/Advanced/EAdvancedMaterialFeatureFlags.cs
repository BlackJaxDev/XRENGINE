namespace XREngine.Rendering;

/// <summary>
/// Material features consumed by kernel selection and native shading.
/// </summary>
[Flags]
public enum EAdvancedMaterialFeatureFlags : uint
{
    None = 0,
    BaseColorTexture = 1u << 0,
    NormalTexture = 1u << 1,
    MetallicRoughnessTexture = 1u << 2,
    Emissive = 1u << 3,
    DoubleSided = 1u << 4,
    ReceivesShadows = 1u << 5,
    CastsShadows = 1u << 6,
    VertexDeformation = 1u << 7,
    Animated = 1u << 8,
}
