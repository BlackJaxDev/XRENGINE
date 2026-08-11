using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class InternedImageViewEntry(ImageView view)
{
    internal ImageView View { get; } = view;
    internal int ReferenceCount { get; set; } = 1;
}
