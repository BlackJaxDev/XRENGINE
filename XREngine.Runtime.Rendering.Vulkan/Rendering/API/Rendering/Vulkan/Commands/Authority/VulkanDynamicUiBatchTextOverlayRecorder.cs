using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records the late native text overlay against frozen output attachments.
/// The recorder owns no renderer, output authority, or planner reference.
/// </summary>
internal sealed unsafe class VulkanDynamicUiBatchTextOverlayRecorder
{
    internal bool TryRecord(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        in VulkanDynamicUiBatchTextOverlayRecordingInput input,
        out CommandBuffer overlayCommandBuffer,
        out bool streamlineUiInitialized)
    {
        overlayCommandBuffer = default;
        streamlineUiInitialized = false;
        if (!input.IsValid)
            return false;

        Result resetResult = encoder.Reset(input.OverlayCommandBuffer);
        if (resetResult != Result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to reset dynamic UI text overlay command buffer: {resetResult}.");
        }

        encoder.Runtime.ResetBindState(encoder, input.OverlayCommandBuffer);
        bool trackingStarted = true;
        try
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            encoder.Runtime.BeginRecording(
                encoder.Runtime.Api,
                encoder.Runtime.DeviceContext.StateMachine,
                input.OverlayCommandBuffer,
                "vkBeginCommandBuffer.DynamicUiTextOverlay");
            if (encoder.Runtime.Api.BeginCommandBuffer(input.OverlayCommandBuffer, ref beginInfo) != Result.Success)
                throw new InvalidOperationException("Failed to begin dynamic UI text overlay command buffer.");

            encoder.Runtime.SeedRecordedImageLayoutState(
                input.OverlayCommandBuffer,
                input.PredecessorCommandBuffer);
            encoder.Runtime.TransitionSecondaryDescriptorImagesForExecution(
                encoder,
                telemetry,
                input.OverlayCommandBuffer,
                input.SecondaryCommandBuffer);
            encoder.Runtime.MergeSecondaryImageStatesForExecution(
                input.OverlayCommandBuffer,
                input.SecondaryCommandBuffer,
                telemetry);

            if (input.Target.HasStreamlineUi)
            {
                RecordSecondaryIntoAttachment(
                    encoder,
                    telemetry,
                    input.OverlayCommandBuffer,
                    input.SecondaryCommandBuffer,
                    input.Target.StreamlineUiImage,
                    input.Target.StreamlineUiView,
                    input.Target.Extent,
                    input.Target.StreamlineUiInitialLayout,
                    ImageLayout.General,
                    input.PreferKhrDynamicRendering);
                streamlineUiInitialized = true;
            }

            RecordSecondaryIntoAttachment(
                encoder,
                telemetry,
                input.OverlayCommandBuffer,
                input.SecondaryCommandBuffer,
                input.Target.SwapchainImage,
                input.Target.SwapchainView,
                input.Target.Extent,
                input.InitialSwapchainLayout,
                ImageLayout.PresentSrcKhr,
                input.PreferKhrDynamicRendering);

            if (encoder.End(input.OverlayCommandBuffer) != Result.Success)
                throw new InvalidOperationException("Failed to end dynamic UI text overlay command buffer.");
            trackingStarted = false;
            overlayCommandBuffer = input.OverlayCommandBuffer;
            return true;
        }
        catch
        {
            if (trackingStarted)
                encoder.Abandon(input.OverlayCommandBuffer);
            throw;
        }
    }

    private static void RecordSecondaryIntoAttachment(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        CommandBuffer secondary,
        Image image,
        ImageView view,
        Extent2D extent,
        ImageLayout initialLayout,
        ImageLayout finalLayout,
        bool preferKhrDynamicRendering)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        encoder.Runtime.EmitImageTransition(
            encoder,
            telemetry,
            commandBuffer,
            image,
            in range,
            initialLayout,
            ImageLayout.ColorAttachmentOptimal);

        RenderingAttachmentInfo colorAttachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        RenderingInfo renderingInfo = new()
        {
            SType = StructureType.RenderingInfo,
            Flags = RenderingFlags.ContentsSecondaryCommandBuffersBit,
            RenderArea = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = extent,
            },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };
        encoder.Track(commandBuffer, ObjectType.ImageView, view.Handle);
        BeginDynamicRendering(encoder, commandBuffer, &renderingInfo, preferKhrDynamicRendering);
        encoder.Track(commandBuffer, ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle));
        encoder.Runtime.Api.CmdExecuteCommands(commandBuffer, 1, &secondary);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExecuteSecondaryCommandBuffers(1);
        EndDynamicRendering(encoder, commandBuffer, preferKhrDynamicRendering);

        encoder.Runtime.EmitImageTransition(
            encoder,
            telemetry,
            commandBuffer,
            image,
            in range,
            ImageLayout.ColorAttachmentOptimal,
            finalLayout);
    }

    private static void BeginDynamicRendering(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        RenderingInfo* renderingInfo,
        bool preferKhrDynamicRendering)
    {
        bool useKhr = preferKhrDynamicRendering || encoder.Runtime.DeviceContext.InstanceApiVersion < Vk.Version13;
        if (!useKhr)
        {
            encoder.Runtime.Api.CmdBeginRendering(commandBuffer, renderingInfo);
            return;
        }

        (encoder.Runtime.DeviceContext.ExtensionFunctions.KhrDynamicRendering ??
            throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded."))
            .CmdBeginRendering(commandBuffer, renderingInfo);
    }

    private static void EndDynamicRendering(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        bool preferKhrDynamicRendering)
    {
        bool useKhr = preferKhrDynamicRendering || encoder.Runtime.DeviceContext.InstanceApiVersion < Vk.Version13;
        if (!useKhr)
        {
            encoder.Runtime.Api.CmdEndRendering(commandBuffer);
            return;
        }

        (encoder.Runtime.DeviceContext.ExtensionFunctions.KhrDynamicRendering ??
            throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded."))
            .CmdEndRendering(commandBuffer);
    }
}

/// <summary>Frozen command/runtime observations required by one late overlay recording.</summary>
internal readonly record struct VulkanDynamicUiBatchTextOverlayRecordingInput(
    CommandBuffer OverlayCommandBuffer,
    CommandBuffer SecondaryCommandBuffer,
    int OperationCount,
    ImageLayout InitialSwapchainLayout,
    CommandBuffer PredecessorCommandBuffer,
    bool PreferKhrDynamicRendering,
    VulkanDynamicUiOverlayTarget Target)
{
    internal bool IsValid => OverlayCommandBuffer.Handle != 0 &&
        SecondaryCommandBuffer.Handle != 0 &&
        OperationCount > 0 &&
        Target.SwapchainImage.Handle != 0 &&
        Target.SwapchainView.Handle != 0;
}
