namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral authored value shapes supported by a material layout.
/// </summary>
public enum EAdvancedMaterialValueKind : uint
{
    UInt = 0,
    Int = 1,
    Float = 2,
    Vector2 = 3,
    Vector3 = 4,
    Vector4 = 5,
    Matrix4x4 = 6,
    Texture = 7,
    Sampler = 8,
}
