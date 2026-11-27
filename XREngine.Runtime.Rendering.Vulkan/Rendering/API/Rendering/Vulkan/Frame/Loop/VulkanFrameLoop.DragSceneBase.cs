using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Writes the last accepted scene image into the acquired swapchain image before a drag-resize frame draws its
/// screen UI.
/// </summary>
/// <remarks>
/// While Win32 runs its modal sizing loop, a desktop frame often records only the screen UI: the scene keeps its
/// resource generation until the drag ends, and the frame does not present the scene again. The UI pass loads the
/// acquired image, and that image holds the frame that last used it, which was recorded at an earlier drag size.
/// Its old UI then shows through the new UI as ghost copies. This base pass blits the retained scene source of the
/// last accepted frame over the whole image first, so the UI draws over a current scene image.
/// </remarks>
internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Records the scene base command buffer for a drag-resize frame that does not present the scene itself.
    /// The command buffer is submitted directly after the acquire wait and before the scene primary. It returns the
    /// swapchain image and the source image to their submitted access states, so the primary keeps its recorded entry
    /// state.
    /// </summary>
    private void TryRecordDesktopDragSceneBase(ref VulkanFrameAttempt attempt)
    {
        attempt.HasDesktopDragSceneBaseCommandBuffer = false;
        attempt.DesktopDragSceneBaseCommandBuffer = default;
        if (!attempt.InteractiveResize ||
            attempt.HasWindowPresentationSourceOwner ||
            attempt.UsesDesktopOffscreenTarget ||
            attempt.SceneCommandBuffer.Handle == 0)
            return;

        CommandBuffer[]? bases = _commandRuntime.CommandBuffers.DesktopDragSceneBases;
        Image[]? swapchainImages = OutputRuntime.Desktop.Images;
        if (bases is null || attempt.ImageIndex >= bases.Length ||
            swapchainImages is null || attempt.ImageIndex >= swapchainImages.Length)
            return;

        _outputRuntime.PresentationSource.Submitted.Capture(
            out VulkanPresentationSourceTuple source,
            out VulkanResidentTemplateDependencyLease? sourceLease);
        if (!ResourceRuntime.TryValidateRetainedPresentationSourceForReplay(source, sourceLease, out _))
            return;

        // The source keeps its submitted state for its next reader, and the swapchain image returns to the state
        // that the scene primary was recorded against.
        if (!_commandRuntime.Synchronization.TryGetSubmittedImageAccessState(
                source.Image, source.Aspect, out VulkanImageAccessState sourceState) ||
            sourceState.Layout == ImageLayout.Undefined)
            return;

        Image swapchainImage = swapchainImages[attempt.ImageIndex];
        if (!_commandRuntime.Synchronization.TryGetSubmittedImageAccessState(
                swapchainImage, ImageAspectFlags.ColorBit, out VulkanImageAccessState swapchainState) ||
            swapchainState.Layout == ImageLayout.Undefined)
            return;

        CommandBuffer commandBuffer = bases[attempt.ImageIndex];
        VulkanTrackedCommandEncoder encoder = new(_commandRuntime);
        if (encoder.Reset(commandBuffer) != Result.Success)
            return;

        bool trackingStarted = false;
        try
        {
            _commandRuntime.BeginRecording(
                Api!,
                _deviceContext.StateMachine,
                commandBuffer,
                "vkBeginCommandBuffer.DesktopDragSceneBase",
                CommandBufferUsageFlags.OneTimeSubmitBit);
            trackingStarted = true;
            if (!ResourceRuntime.TryAdoptRetainedPresentationImageForRecording(
                    commandBuffer, source, sourceLease, out _))
            {
                encoder.Abandon(commandBuffer);
                return;
            }

            RecordDesktopDragSceneBlit(
                encoder,
                commandBuffer,
                in source,
                in sourceState,
                swapchainImage,
                in swapchainState,
                OutputRuntime.Desktop.Extent);

            bool published = encoder.TryEnd(
                commandBuffer,
                cacheVariant: false,
                out Result endResult,
                out _);
            trackingStarted = false;
            if (endResult != Result.Success || !published)
                return;
        }
        catch
        {
            if (trackingStarted)
                encoder.Abandon(commandBuffer);
            throw;
        }

        attempt.DesktopDragSceneBaseCommandBuffer = commandBuffer;
        attempt.HasDesktopDragSceneBaseCommandBuffer = true;
    }

    /// <summary>
    /// Blits the whole source image into the whole swapchain image, and then restores both submitted access states.
    /// The swapchain transition joins the acquire semaphore wait at color attachment output.
    /// </summary>
    private void RecordDesktopDragSceneBlit(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        in VulkanPresentationSourceTuple source,
        in VulkanImageAccessState sourceState,
        Image swapchainImage,
        in VulkanImageAccessState swapchainState,
        Extent2D swapchainExtent)
    {
        ImageSubresourceRange sourceRange = new()
        {
            AspectMask = source.Aspect,
            LevelCount = 1,
            LayerCount = 1,
        };
        ImageSubresourceRange colorRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };

        VulkanImageAccessState sourceTransfer = sourceState with
        {
            Layout = ImageLayout.TransferSrcOptimal,
            StageMask = PipelineStageFlags2.TransferBit,
            AccessMask = AccessFlags2.TransferReadBit,
            ExpectedDescriptorLayout = ImageLayout.Undefined,
            Serial = NextImageTransitionSerial(),
        };
        _commandRuntime.EmitImageStateTransition(
            encoder, _telemetry, commandBuffer, source.Image, in sourceRange, in sourceState, in sourceTransfer);
        // The blit replaces every pixel, so the old contents are discarded.
        _commandRuntime.EmitAcquiredSwapchainImageTransition(
            encoder,
            _telemetry,
            commandBuffer,
            swapchainImage,
            in colorRange,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal);

        ImageBlit region = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = source.Aspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        region.SrcOffsets.Element1 = new Offset3D(checked((int)source.Width), checked((int)source.Height), 1);
        region.DstOffsets.Element1 = new Offset3D(
            checked((int)swapchainExtent.Width),
            checked((int)swapchainExtent.Height),
            1);
        encoder.BlitImage(
            commandBuffer,
            source.Image,
            ImageLayout.TransferSrcOptimal,
            swapchainImage,
            ImageLayout.TransferDstOptimal,
            ref region,
            Filter.Linear);

        VulkanImageAccessState sourceRestored = sourceState with { Serial = NextImageTransitionSerial() };
        _commandRuntime.EmitImageStateTransition(
            encoder, _telemetry, commandBuffer, source.Image, in sourceRange, in sourceTransfer, in sourceRestored);
        VulkanImageAccessState swapchainTransfer = swapchainState with
        {
            Layout = ImageLayout.TransferDstOptimal,
            StageMask = PipelineStageFlags2.TransferBit,
            AccessMask = AccessFlags2.TransferWriteBit,
            ExpectedDescriptorLayout = ImageLayout.Undefined,
        };
        VulkanImageAccessState swapchainRestored = swapchainState with { Serial = NextImageTransitionSerial() };
        _commandRuntime.EmitImageStateTransition(
            encoder, _telemetry, commandBuffer, swapchainImage, in colorRange, in swapchainTransfer, in swapchainRestored);
    }

    private ulong NextImageTransitionSerial()
        => unchecked((ulong)Interlocked.Increment(ref _telemetry._vulkanImageLayoutTransitionSerial));
}
