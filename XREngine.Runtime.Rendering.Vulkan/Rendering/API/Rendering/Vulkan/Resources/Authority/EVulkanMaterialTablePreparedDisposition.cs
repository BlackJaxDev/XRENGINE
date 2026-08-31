namespace XREngine.Rendering.Vulkan;

/// <summary>Result class for material-table lowering when a native backing is not immediately ready.</summary>
internal enum EVulkanMaterialTablePreparedDisposition
{
    Ready,
    Pending,
    Failed,
}
