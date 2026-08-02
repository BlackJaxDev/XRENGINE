namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Specifies the kind of mesh frame data stream in Vulkan rendering.
/// </summary>
internal enum EVulkanMeshFrameDataStreamKind : byte
{
    /// <summary>
    /// Specifies the primary mesh frame data stream, which contains the main vertex and index data for rendering.
    /// </summary>
    Primary,
    /// <summary>
    /// Specifies a dynamic UI mesh frame data stream, which is used for rendering user interface elements that may change frequently during runtime.
    /// </summary>
    DynamicUi,
}