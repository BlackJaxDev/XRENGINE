using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanFrameBufferPlan(FrameBufferResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public IReadOnlyList<FrameBufferAttachmentDescriptor> Attachments => Descriptor.Attachments;
}
