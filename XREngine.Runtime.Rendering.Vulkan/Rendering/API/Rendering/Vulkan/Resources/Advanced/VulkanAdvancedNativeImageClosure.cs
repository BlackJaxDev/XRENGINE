using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen image and view generations consumed by a native shading closure.</summary>
internal readonly record struct VulkanAdvancedNativeImageClosure(
    Image Image,
    ulong ImageGeneration,
    ImageView View,
    ulong ViewGeneration)
{
    internal bool IsValid
        => Image.Handle != 0 && ImageGeneration != 0 &&
           View.Handle != 0 && ViewGeneration != 0;

    /// <summary>Compares all fields. The check compares the native handles by value, so it does not box them.</summary>
    public bool Equals(VulkanAdvancedNativeImageClosure other)
        => Image.Handle == other.Image.Handle &&
           ImageGeneration == other.ImageGeneration &&
           View.Handle == other.View.Handle &&
           ViewGeneration == other.ViewGeneration;

    /// <summary>Combines the handle values and generations without boxing.</summary>
    public override int GetHashCode()
        => HashCode.Combine(Image.Handle, ImageGeneration, View.Handle, ViewGeneration);
}
