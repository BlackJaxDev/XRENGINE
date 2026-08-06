using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct DeferredSecondaryCommandBuffer
{
    public DeferredSecondaryCommandBuffer(
        CommandPool pool,
        CommandBuffer commandBuffer,
        ulong resourceGeneration)
    {
        Pool = pool;
        CommandBuffer = commandBuffer;
        ResourceGeneration = resourceGeneration;
        ArtifactRetirement = default;
    }

    public DeferredSecondaryCommandBuffer(
        in VulkanRecordedCommandArtifactRetirement artifactRetirement,
        ulong resourceGeneration)
    {
        Pool = artifactRetirement.OwnerPool;
        CommandBuffer = artifactRetirement.NativeBuffer;
        ResourceGeneration = resourceGeneration;
        ArtifactRetirement = artifactRetirement;
    }

    public CommandPool Pool { get; }
    public CommandBuffer CommandBuffer { get; }
    public ulong ResourceGeneration { get; }
    public VulkanRecordedCommandArtifactRetirement ArtifactRetirement { get; }
    public bool HasArtifactOwner => ArtifactRetirement.IsValid;
}
