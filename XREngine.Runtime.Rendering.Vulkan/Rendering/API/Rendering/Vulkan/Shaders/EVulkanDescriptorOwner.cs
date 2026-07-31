namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Logical owner of one descriptor-set tier in the linked program contract.
/// </summary>
internal enum EVulkanDescriptorOwner : byte
{
    Globals = 0,
    Compute,
    Material,
    Pass,
    Frame,
    View,
    Object,
    Instance,
    RuntimeCallback,
}
