namespace XREngine.Rendering.Vulkan;

/// <summary>Generation-specific identity for one imported texture upload.</summary>
internal readonly record struct VulkanTextureUploadTicket(long Sequence, long StreamingGeneration)
{
    public bool IsValid => Sequence > 0;
}
