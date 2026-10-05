using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Call-local view descriptors for one OpenXR view-family planning pass.
/// </summary>
/// <remarks>
/// Planning runs on the collect, render and diagnostic threads at the same time,
/// so its descriptors must never share instance storage.
/// </remarks>
[InlineArray(RenderFrameViewSet.MaxViewCount)]
internal struct OpenXrViewDescriptorInlineArray
{
    private RenderFrameViewDescriptor _element0;
}
