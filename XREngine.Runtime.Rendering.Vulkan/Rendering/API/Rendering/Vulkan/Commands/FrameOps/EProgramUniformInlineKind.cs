namespace XREngine.Rendering.Vulkan;

internal enum EProgramUniformInlineKind : byte
{
    None,
    Float,
    Int,
    UInt,
    Double,
    Vector2,
    Vector3,
    Vector4,
    Matrix4x4,
    DVector4,
    IVector4,
    UVector4,
}
