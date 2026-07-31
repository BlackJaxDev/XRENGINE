namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the typed source selected for a reflected auto-uniform member.
/// </summary>
internal enum EVulkanAutoUniformSourceKind : byte
{
    Unsupported = 0,
    Engine,
    TemporalViewProjection,
    MeshState,
    MaterialOrRuntime,
    StructSnapshot,
}
