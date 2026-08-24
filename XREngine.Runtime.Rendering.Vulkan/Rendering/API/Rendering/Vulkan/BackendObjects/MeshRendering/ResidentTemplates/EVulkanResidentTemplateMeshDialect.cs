namespace XREngine.Rendering.Vulkan;

/// <summary>Mesh-command encoding consumed by a resident Vulkan draw template.</summary>
internal enum EVulkanResidentTemplateMeshDialect : byte
{
    VertexInput,
    ShaderGeneratedVertices,
    Indirect,
    MeshTasks,
}
