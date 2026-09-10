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
}
