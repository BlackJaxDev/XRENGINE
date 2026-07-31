namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Declares the owner whose generation controls publication of a binding value.
/// </summary>
internal enum EVulkanBindingFrequency : byte
{
    Unknown = 0,
    Frame,
    View,
    Pass,
    Material,
    Object,
    Instance,
    RuntimeCallback,
    Count,
}
