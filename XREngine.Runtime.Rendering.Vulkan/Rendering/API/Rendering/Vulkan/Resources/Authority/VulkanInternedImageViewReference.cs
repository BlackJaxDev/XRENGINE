using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies one acquired interned view by its native handle and published generation.</summary>
internal readonly record struct VulkanInternedImageViewReference(ImageView View, ulong Generation)
{
    internal bool IsValid => View.Handle != 0 && Generation != 0;
}
