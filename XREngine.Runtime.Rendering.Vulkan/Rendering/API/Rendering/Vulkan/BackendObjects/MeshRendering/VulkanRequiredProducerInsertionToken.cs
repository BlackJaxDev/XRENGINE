namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies a mesh request's position in one required producer batch.</summary>
internal readonly record struct VulkanRequiredProducerInsertionToken(
    ulong BatchGeneration,
    int DirectOperationCount)
{
    internal bool IsValid => BatchGeneration != 0UL;
}
