namespace XREngine.Rendering;

/// <summary>
/// Visibility-local shader storage bindings. They follow the global advanced
/// table range so OpenGL can bind both namespaces in one program; Vulkan places
/// them in the visibility descriptor set.
/// </summary>
public static class AdvancedVisibilityShaderBindings
{
    public const uint Payloads = 28u;
    public const uint Counters = 29u;
    public const uint MeshPayloadIndices = 30u;
    public const uint Meshlets = 31u;
    public const uint MeshletVertexIndices = 32u;
    public const uint MeshletTriangleIndices = 33u;
    public const uint CurrentPositions = 34u;
    public const uint PreviousPositions = 35u;
    public const uint TextureCoordinates = 36u;
}
