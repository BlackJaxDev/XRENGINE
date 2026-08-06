using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free atomic publication for the final presentation tuple.
/// </summary>
internal sealed class VulkanPresentationSourcePublication
{
    private readonly object _sync = new();
    private VulkanPresentationSourceTuple _current;
    private ulong _nextEpoch;

    internal VulkanPresentationSourceTuple PublishLogical(
        in VulkanPresentationSourceTuple source)
    {
        lock (_sync)
        {
            ulong epoch = ++_nextEpoch;
            if (epoch == 0)
                epoch = ++_nextEpoch;

            _current = source with { LogicalEpoch = epoch };
            return _current;
        }
    }

    internal bool TryBindDescriptor(
        ulong expectedLogicalEpoch,
        in DescriptorImageInfo imageInfo,
        DescriptorSet descriptorSet,
        ulong descriptorSetGeneration,
        int descriptorSlot,
        ulong descriptorPublicationGeneration,
        CommandBuffer commandArtifact,
        ulong commandArtifactGeneration,
        out VulkanPresentationSourceTuple source)
    {
        lock (_sync)
        {
            if (_current.LogicalEpoch != expectedLogicalEpoch ||
                _current.ImageView.Handle != imageInfo.ImageView.Handle ||
                _current.Sampler.Handle != imageInfo.Sampler.Handle)
            {
                source = _current;
                return false;
            }

            _current = _current with
            {
                ExpectedLayout = imageInfo.ImageLayout,
                DescriptorSet = descriptorSet,
                DescriptorSetGeneration = descriptorSetGeneration,
                DescriptorSlot = descriptorSlot,
                DescriptorPublicationGeneration = descriptorPublicationGeneration,
                OwningCommandArtifact = commandArtifact,
                OwningCommandArtifactGeneration = commandArtifactGeneration,
            };
            source = _current;
            return true;
        }
    }

    internal VulkanPresentationSourceTuple Capture()
    {
        lock (_sync)
            return _current;
    }
}
