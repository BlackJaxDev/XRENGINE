using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly struct DeferredSecondaryCommandBuffer
        {
            public DeferredSecondaryCommandBuffer(
                CommandPool pool,
                CommandBuffer commandBuffer)
            {
                Pool = pool;
                CommandBuffer = commandBuffer;
                ArtifactRetirement = default;
            }

            public DeferredSecondaryCommandBuffer(
                in VulkanRecordedCommandArtifactRetirement artifactRetirement)
            {
                Pool = artifactRetirement.OwnerPool;
                CommandBuffer = artifactRetirement.NativeBuffer;
                ArtifactRetirement = artifactRetirement;
            }

            public CommandPool Pool { get; }
            public CommandBuffer CommandBuffer { get; }
            public VulkanRecordedCommandArtifactRetirement ArtifactRetirement { get; }
            public bool HasArtifactOwner => ArtifactRetirement.IsValid;
        }

    }
}
