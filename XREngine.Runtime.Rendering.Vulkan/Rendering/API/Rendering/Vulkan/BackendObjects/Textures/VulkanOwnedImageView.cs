using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies the native generation owned with an image view handle.</summary>
internal readonly record struct VulkanOwnedImageView(ImageView View, ulong Generation)
{
    public ulong Handle => View.Handle;
    public bool IsValid => View.Handle != 0 && Generation != 0;

    public static implicit operator ImageView(VulkanOwnedImageView owned) => owned.View;
}
