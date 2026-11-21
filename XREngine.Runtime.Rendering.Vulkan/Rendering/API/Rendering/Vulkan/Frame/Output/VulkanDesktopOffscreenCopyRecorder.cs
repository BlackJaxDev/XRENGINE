using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records the copy from the desktop offscreen color target into the acquired
/// swapchain image. The command buffer runs directly after the scene primary in
/// the same submission, so it seeds its image state from that primary. It is the
/// first command in the submission that touches the acquired image, so the
/// acquired-image transition joins the acquire semaphore wait stage.
/// </summary>
internal static class VulkanDesktopOffscreenCopyRecorder
{
    internal static void Record(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        in VulkanDesktopOffscreenCopyInput input)
    {
        Result reset = encoder.Reset(input.CopyCommandBuffer);
        if (reset != Result.Success)
            throw new InvalidOperationException($"Failed to reset the desktop offscreen copy command buffer: {reset}.");

        VulkanCommandRuntime runtime = encoder.Runtime;
        bool trackingStarted = false;
        try
        {
            runtime.BeginRecording(
                runtime.Api,
                runtime.DeviceContext.StateMachine,
                input.CopyCommandBuffer,
                "vkBeginCommandBuffer.DesktopOffscreenCopy",
                CommandBufferUsageFlags.OneTimeSubmitBit);
            trackingStarted = true;
            runtime.SeedRecordedImageLayoutState(input.CopyCommandBuffer, input.SceneCommandBuffer);

            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            };

            // The scene normally ends with the offscreen image in TransferSrcOptimal,
            // and its final barrier already makes the color writes visible to transfer.
            if (input.OffscreenLayoutAfterScene != ImageLayout.TransferSrcOptimal)
            {
                runtime.EmitImageTransition(
                    encoder,
                    telemetry,
                    input.CopyCommandBuffer,
                    input.OffscreenImage,
                    in range,
                    input.OffscreenLayoutAfterScene,
                    ImageLayout.TransferSrcOptimal);
            }

            runtime.EmitAcquiredSwapchainImageTransition(
                encoder,
                telemetry,
                input.CopyCommandBuffer,
                input.SwapchainImage,
                in range,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal);

            ImageSubresourceLayers layers = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            ImageCopy region = new()
            {
                SrcSubresource = layers,
                DstSubresource = layers,
                Extent = new Extent3D(input.Extent.Width, input.Extent.Height, 1),
            };
            encoder.CopyImage(input.CopyCommandBuffer, input.OffscreenImage, input.SwapchainImage, ref region);

            runtime.EmitImageTransition(
                encoder,
                telemetry,
                input.CopyCommandBuffer,
                input.SwapchainImage,
                in range,
                ImageLayout.TransferDstOptimal,
                input.SwapchainFinalLayout);

            bool published = encoder.TryEnd(
                input.CopyCommandBuffer,
                cacheVariant: false,
                out Result endResult,
                out string publicationFailure);
            trackingStarted = false;
            if (endResult != Result.Success)
                throw new InvalidOperationException($"Failed to end the desktop offscreen copy command buffer: {endResult}.");
            if (!published)
                throw new InvalidOperationException($"The desktop offscreen copy was not published: {publicationFailure}");
        }
        catch
        {
            if (trackingStarted)
                encoder.Abandon(input.CopyCommandBuffer);
            throw;
        }
    }
}
