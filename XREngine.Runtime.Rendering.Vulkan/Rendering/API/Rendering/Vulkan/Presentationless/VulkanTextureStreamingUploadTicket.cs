namespace XREngine.Rendering.Vulkan;

/// <summary>Public identity of one renderer-owned imported texture upload.</summary>
public readonly record struct VulkanTextureStreamingUploadTicket(long Sequence, long StreamingGeneration)
{
    public bool IsValid => Sequence > 0;
}
