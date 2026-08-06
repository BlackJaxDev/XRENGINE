using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Inline attachment storage used by recorded-packet keys. Overflow is rejected
/// instead of allocating or accepting an incomplete reusable identity.
/// </summary>
[InlineArray(VulkanRecordedRenderTargetSnapshot.MaxAttachmentCount)]
internal struct VulkanNativeAttachmentIdentityBuffer
{
    private VulkanNativeAttachmentIdentity _element0;
}
