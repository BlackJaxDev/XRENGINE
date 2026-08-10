using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Publishes the exact descriptor and command-buffer identity that records the
/// final presentation source. Mesh wrappers retain this narrow capability
/// instead of the output, command, or resource authorities behind it.
/// </summary>
internal sealed class VulkanFinalPresentationDescriptorPort(
    VulkanPresentationSourcePublication publication,
    VulkanResourceRuntime resources,
    VulkanCommandRuntime commands)
{
    internal void Observe(
        int descriptorSlot,
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        string? bindingName,
        in DescriptorImageInfo imageInfo,
        ulong resourceSignature,
        bool writeSucceeded)
    {
        if (!writeSucceeded ||
            !string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
        {
            return;
        }

        VulkanPresentationSourceTuple current = publication.CaptureLogical();
        _ = publication.TryBindDescriptor(
            current.LogicalEpoch,
            imageInfo,
            descriptorSet,
            resources.GetPublishedGeneration(ObjectType.DescriptorSet, descriptorSet.Handle),
            descriptorSlot,
            resourceSignature,
            commandBuffer,
            commands.ResolveCommandBufferRecordingGeneration(commandBuffer),
            out _);
    }
}
