using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

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
        WindowPresentationSourceMarker windowPresentationSourceMarker,
        bool writeMatched,
        bool writeSucceeded,
        string? programName = null)
    {
        bool trace = VulkanMeshRenderingConventions.DescriptorTraceEnabled;
        if (!writeSucceeded)
        {
            if (trace)
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentationPort.WriteFailed.{GetHashCode()}.{programName}.{bindingName}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] final presentation observe skipped: write failed prog='{0}' slot={1} set={2} binding={3} name='{4}'.",
                    programName ?? "<null>", descriptorSlot, set, binding, bindingName ?? "<null>");
            return;
        }

        if (!string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
        {
            if (trace)
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentationPort.BindingName.{GetHashCode()}.{programName}.{bindingName}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] final presentation observe skipped: non-source binding prog='{0}' slot={1} set={2} binding={3} name='{4}'.",
                    programName ?? "<null>", descriptorSlot, set, binding, bindingName ?? "<null>");
            return;
        }

        if (!windowPresentationSourceMarker.HasSource)
        {
            if (trace)
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentationPort.NoMarker.{GetHashCode()}.{programName}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] final presentation observe skipped: SourceTexture has no window marker prog='{0}' slot={1}.",
                    programName ?? "<null>", descriptorSlot);
            return;
        }

        VulkanPresentationSourceTuple current = publication.CaptureLogical();
        if (!windowPresentationSourceMarker.HasDeferredAuthority ||
            !ReferenceEquals(current.ColorTexture, windowPresentationSourceMarker.SourceTexture) ||
            (windowPresentationSourceMarker.SourceFrameBuffer is not null &&
             !ReferenceEquals(current.FrameBuffer, windowPresentationSourceMarker.SourceFrameBuffer)) ||
            !ReferenceEquals(current.PresentationPublisher, windowPresentationSourceMarker.Publisher) ||
            current.PresentationPublicationToken != windowPresentationSourceMarker.PublicationToken)
        {
            if (trace)
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentationPort.LogicalMismatch.{GetHashCode()}.{programName}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] final presentation observe skipped: logical source mismatch prog='{0}' slot={1} currentTexture={2} markerTexture={3} currentFbo={4} markerFbo={5} currentPublisher={6} markerPublisher={7} currentToken={8} markerToken={9} epoch={10}.",
                    programName ?? "<null>", descriptorSlot,
                    current.ColorTexture is null ? 0 : RuntimeHelpers.GetHashCode(current.ColorTexture),
                    windowPresentationSourceMarker.SourceTexture is null ? 0 : RuntimeHelpers.GetHashCode(windowPresentationSourceMarker.SourceTexture),
                    current.FrameBuffer is null ? 0 : RuntimeHelpers.GetHashCode(current.FrameBuffer),
                    windowPresentationSourceMarker.SourceFrameBuffer is null ? 0 : RuntimeHelpers.GetHashCode(windowPresentationSourceMarker.SourceFrameBuffer),
                    current.PresentationPublisher is null ? 0 : RuntimeHelpers.GetHashCode(current.PresentationPublisher),
                    windowPresentationSourceMarker.Publisher is null ? 0 : RuntimeHelpers.GetHashCode(windowPresentationSourceMarker.Publisher),
                    current.PresentationPublicationToken,
                    windowPresentationSourceMarker.PublicationToken,
                    current.LogicalEpoch);
            return;
        }
        ulong backingImageHandle = resources.ResolveImageViewBackingImageHandle(imageInfo.ImageView);
        bool nativeAuthorityUnresolved = current.Image.Handle == 0 &&
            current.ImageView.Handle == 0 && current.Sampler.Handle == 0;
        bool viewMatches = nativeAuthorityUnresolved
            ? backingImageHandle != 0 && imageInfo.ImageView.Handle != 0 && imageInfo.Sampler.Handle != 0
            : current.ImageView.Handle == imageInfo.ImageView.Handle ||
              (backingImageHandle != 0 && current.Image.Handle == backingImageHandle);
        if (!viewMatches)
        {
            if (trace)
                Debug.VulkanEvery(
                    $"Vulkan.FinalPresentationPort.UnrelatedSource.{GetHashCode()}.{programName}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanDescriptor] Final presentation source does not match draw. prog='{0}' slot={1} epoch={2} sourcePipeline={3} sourceViewport={4} sourceOutput={5} sourceImage=0x{6:X} sourceView=0x{7:X} drawImage=0x{8:X} drawView=0x{9:X}.",
                    programName ?? "<null>", descriptorSlot, current.LogicalEpoch,
                    current.Context.PipelineIdentity, current.Context.ViewportIdentity,
                    current.Context.OutputTargetIdentity, current.Image.Handle,
                    current.ImageView.Handle, backingImageHandle, imageInfo.ImageView.Handle);
            return;
        }

        ulong imageViewGeneration = resources.GetPublishedGeneration(ObjectType.ImageView, imageInfo.ImageView.Handle);
        ulong samplerGeneration = resources.GetPublishedGeneration(ObjectType.Sampler, imageInfo.Sampler.Handle);
        ulong backingImageGeneration = backingImageHandle == 0
            ? 0
            : resources.GetPublishedGeneration(ObjectType.Image, backingImageHandle);
        _ = resources.TryGetImageAllocationExtent(
            backingImageHandle,
            out Extent3D backingImageExtent);
        if (!resources.TryGetImageAllocationFormat(
                backingImageHandle, out Format backingImageFormat, out SampleCountFlags backingImageSamples))
        {
            return;
        }
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
                backingImageGeneration,
                backingImageExtent,
                backingImageFormat,
                backingImageSamples,
                out _);
        if (trace)
            Debug.VulkanEvery(
                $"Vulkan.FinalPresentationPort.Observe.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[VulkanDescriptor] final presentation observe: prog='{0}' slot={1} targetSlot={2} bound={3} epoch={4} sourcePipeline={5} sourceViewport={6} sourceOutput={7} set=0x{8:X} view=0x{9:X} sampler=0x{10:X} tex='{11}'.",
                programName ?? "<null>", descriptorSlot, targetSlot, bound, current.LogicalEpoch,
                current.Context.PipelineIdentity, current.Context.ViewportIdentity,
                current.Context.OutputTargetIdentity, descriptorSet.Handle,
                imageInfo.ImageView.Handle, imageInfo.Sampler.Handle,
                current.ColorTexture?.Name ?? "<null>");
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
