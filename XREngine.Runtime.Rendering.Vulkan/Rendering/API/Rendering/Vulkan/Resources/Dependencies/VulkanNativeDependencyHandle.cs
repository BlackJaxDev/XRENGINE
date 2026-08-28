namespace XREngine.Rendering.Vulkan;

/// <summary>ABA-safe identity assigned by the Vulkan native-owner graph.</summary>
internal readonly record struct VulkanNativeDependencyHandle(uint Slot, uint Generation)
{
    internal bool IsValid => Slot != 0 && Generation != 0;
}
