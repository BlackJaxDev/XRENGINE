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
    VulkanCommandRuntime commands,
    VulkanFinalPresentationLedgerState ledger,
    Func<DesktopFrameActivitySnapshot> captureFrameActivity)
{
    internal void Observe(
        int descriptorSlot,
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint set,
        uint binding,
        string? bindingName,
        in DescriptorImageInfo imageInfo,
        ulong resourceSignature,
        bool writeMatched,
        bool writeSucceeded)
    {
        if (!writeSucceeded ||
            !string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
        {
            return;
        }

        VulkanPresentationSourceTuple current = publication.CaptureLogical();
        if (!publication.TryBindDescriptor(
                current.LogicalEpoch,
                imageInfo,
                descriptorSet,
                resources.GetPublishedGeneration(ObjectType.DescriptorSet, descriptorSet.Handle),
                descriptorSlot,
                resourceSignature,
                commandBuffer,
                commands.ResolveCommandBufferRecordingGeneration(commandBuffer),
                out _))
        {
            return;
        }

        if (!ledger.Enabled)
            return;

        DesktopFrameActivitySnapshot activity = captureFrameActivity();
        if (!activity.IsActive)
            return;

        ledger.ObserveDescriptor(
            activity.FrameNumber,
            descriptorSlot,
            unchecked((ulong)commandBuffer.Handle),
            descriptorSet.Handle,
            set,
            binding,
            bindingName,
            imageInfo,
            resourceSignature,
            writeMatched,
            writeSucceeded);
    }
}
