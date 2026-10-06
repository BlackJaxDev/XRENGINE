using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class InternedImageViewEntry(ImageView view, ulong generation)
{
    internal ImageView View { get; } = view;
    internal ulong Generation { get; } = generation;
    internal int ReferenceCount { get; set; } = 1;
}
