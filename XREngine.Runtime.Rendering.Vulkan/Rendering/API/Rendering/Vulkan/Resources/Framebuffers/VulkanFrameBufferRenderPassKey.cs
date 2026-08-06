namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanFrameBufferRenderPassKey(
    FrameBufferAttachmentSignature[] Attachments) : IEquatable<VulkanFrameBufferRenderPassKey>
{
    public bool Equals(VulkanFrameBufferRenderPassKey other)
    {
        if (Attachments.Length != other.Attachments.Length)
            return false;

        for (int i = 0; i < Attachments.Length; i++)
            if (!Attachments[i].Equals(other.Attachments[i]))
                return false;

        return true;
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (FrameBufferAttachmentSignature attachment in Attachments)
            hash.Add(attachment);
        return hash.ToHashCode();
    }
}
