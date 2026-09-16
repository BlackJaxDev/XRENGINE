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
        bool writeSucceeded,
        string? programName = null)
    {
        if (!writeSucceeded ||
            !string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
        {
            return;
        }

        VulkanPresentationSourceTuple current = publication.CaptureLogical();
        ulong backingImageHandle = resources.ResolveImageViewBackingImageHandle(imageInfo.ImageView);
        bool viewMatches = current.ImageView.Handle == imageInfo.ImageView.Handle ||
            (backingImageHandle != 0 && current.Image.Handle == backingImageHandle);
        if (!viewMatches)
            return;

        ulong imageViewGeneration = resources.GetPublishedGeneration(ObjectType.ImageView, imageInfo.ImageView.Handle);
        ulong samplerGeneration = resources.GetPublishedGeneration(ObjectType.Sampler, imageInfo.Sampler.Handle);
        int targetSlot = commands.ResolveCommandBufferImageIndex(commandBuffer);
        if (targetSlot < 0)
            targetSlot = descriptorSlot;

        bool bound = publication.TryBindDescriptor(
                current.LogicalEpoch,
                imageInfo,
                descriptorSet,
                resources.GetPublishedGeneration(ObjectType.DescriptorSet, descriptorSet.Handle),
                targetSlot,
                resourceSignature,
                commandBuffer,
                commands.ResolveCommandBufferRecordingGeneration(commandBuffer),
                imageViewGeneration,
                samplerGeneration,
                backingImageHandle,
                out _);
        Debug.VulkanEvery(
            $"Vulkan.FinalPresentationPort.Observe.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] FinalPresentationDescriptorPort.Observe: prog='{0}' slot={1} targetSlot={2} bound={3} currentEpoch={4} set=0x{5:X} view=0x{6:X} sampler=0x{7:X} tex='{8}'",
            programName ?? "<null>", descriptorSlot, targetSlot, bound, current.LogicalEpoch, descriptorSet.Handle, imageInfo.ImageView.Handle, imageInfo.Sampler.Handle, current.ColorTexture?.Name ?? "<null>");
        if (!bound)
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
            targetSlot,
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
