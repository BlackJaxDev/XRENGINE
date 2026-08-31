using ImGuiNET;
using Silk.NET.Vulkan;
using System.Numerics;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records one desktop ImGui overlay from a frozen output target.  The recorder
/// has no renderer facade dependency; the frame loop supplies command, resource,
/// and output authorities explicitly for every invocation.
/// </summary>
internal sealed unsafe class VulkanImGuiOverlayCommandRecorder
{
    internal bool TryRecord(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        VulkanImGuiDrawBufferResources drawBuffers,
        in VulkanImGuiOverlayRecordingInput input,
        out CommandBuffer overlayCommandBuffer)
    {
        overlayCommandBuffer = default;
        if (!input.IsValid || !VulkanImGuiOverlayAdmission.HasRenderableSnapshot(input.Snapshot))
            return false;

        // Font creation remains an output-owned one-time operation.  A pipeline
        // must have been admitted by the output lifecycle before a command is
        // recorded; this prevents a stale pre-recreation pipeline from being used.
        if (input.Resources.Pipeline.Handle == 0 || input.Resources.PipelineLayout.Handle == 0)
            return false;

        Result reset = encoder.Reset(input.OverlayCommandBuffer);
        if (reset != Result.Success)
            throw new InvalidOperationException($"Failed to reset ImGui overlay command buffer: {reset}.");

        bool trackingStarted = false;
        try
        {
            encoder.Runtime.BeginRecording(
                encoder.Runtime.Api,
                encoder.Runtime.DeviceContext.StateMachine,
                input.OverlayCommandBuffer,
                "vkBeginCommandBuffer.ImGuiOverlay",
                CommandBufferUsageFlags.OneTimeSubmitBit);
            trackingStarted = true;

            encoder.Runtime.SeedRecordedImageLayoutState(
                input.OverlayCommandBuffer,
                input.PredecessorCommandBuffer);

            if (input.Target.HasStreamlineUi)
            {
                RecordIntoAttachment(
                    encoder, telemetry, drawBuffers, in input,
                    input.Target.StreamlineUiImage,
                    input.Target.StreamlineUiView,
                    input.Target.StreamlineUiInitialLayout,
                    ImageLayout.General,
                    AttachmentLoadOp.Clear,
                    clear: true,
                    waitsOnExternalAcquire: false);
            }

            RecordIntoAttachment(
                encoder, telemetry, drawBuffers, in input,
                input.Target.SwapchainImage,
                input.Target.SwapchainView,
                input.InitialSwapchainLayout,
                ImageLayout.PresentSrcKhr,
                input.ClearSwapchain ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                clear: input.ClearSwapchain,
                waitsOnExternalAcquire:
                    input.PredecessorCommandBuffer.Handle == 0);

            bool published = encoder.TryEnd(
                input.OverlayCommandBuffer,
                cacheVariant: true,
                out Result endResult,
                out string publicationFailure);
            trackingStarted = false;
            if (endResult != Result.Success)
                throw new InvalidOperationException("Failed to end ImGui overlay command buffer.");
            if (!published)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.ImGui.CommandPublicationRetirement",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan.ImGui] Discarded an overlay command buffer because a dependency retired during recording: {0}",
                    publicationFailure);
                return false;
            }

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

    private static void RecordIntoAttachment(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        VulkanImGuiDrawBufferResources drawBuffers,
        in VulkanImGuiOverlayRecordingInput input,
        Image image,
        ImageView view,
        ImageLayout initialLayout,
        ImageLayout finalLayout,
        AttachmentLoadOp loadOp,
        bool clear,
        bool waitsOnExternalAcquire)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };
        if (waitsOnExternalAcquire)
        {
            encoder.Runtime.EmitAcquiredSwapchainImageTransition(
                encoder,
                telemetry,
                input.OverlayCommandBuffer,
                image,
                in range,
                initialLayout,
                ImageLayout.ColorAttachmentOptimal);
        }
        else
        {
            encoder.Runtime.EmitImageTransition(
                encoder, telemetry, input.OverlayCommandBuffer, image, in range,
                initialLayout, ImageLayout.ColorAttachmentOptimal);
        }

        RenderingAttachmentInfo attachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = loadOp,
            StoreOp = AttachmentStoreOp.Store,
        };
        if (clear)
            attachment.ClearValue = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 0f) };

        RenderingInfo rendering = new()
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = input.Target.Extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };
        encoder.Track(input.OverlayCommandBuffer, ObjectType.ImageView, view.Handle);
        BeginDynamicRendering(encoder, input.OverlayCommandBuffer, &rendering, input.PreferKhrDynamicRendering);
        RenderSnapshot(encoder, drawBuffers, in input);
        EndDynamicRendering(encoder, input.OverlayCommandBuffer, input.PreferKhrDynamicRendering);

        encoder.Runtime.EmitImageTransition(
            encoder, telemetry, input.OverlayCommandBuffer, image, in range,
            ImageLayout.ColorAttachmentOptimal, finalLayout);
    }

    private static void RenderSnapshot(
        VulkanTrackedCommandEncoder encoder,
        VulkanImGuiDrawBufferResources drawBuffers,
        in VulkanImGuiOverlayRecordingInput input)
    {
        ref VulkanImGuiDrawBufferSet buffers = ref drawBuffers.Ensure(
            input.ImageIndex,
            checked((ulong)input.Snapshot.TotalVertexCount * (ulong)sizeof(ImDrawVert)),
            checked((ulong)input.Snapshot.TotalIndexCount * sizeof(ushort)));
        drawBuffers.Upload(input.Snapshot, ref buffers);

        encoder.BindPipeline(input.OverlayCommandBuffer, input.Resources.Pipeline);
        encoder.BindVertexBuffer(input.OverlayCommandBuffer, 0, buffers.VertexBuffer);
        encoder.BindIndexBuffer(input.OverlayCommandBuffer, buffers.IndexBuffer, IndexType.Uint16);

        Vector2 displaySize = input.Snapshot.DisplaySize;
        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            return;

        VulkanImGuiPushConstants constants = new()
        {
            Scale = new Vector2(2.0f / displaySize.X, 2.0f / displaySize.Y),
            Translate = new Vector2(
                -1.0f - input.Snapshot.DisplayPos.X * (2.0f / displaySize.X),
                -1.0f - input.Snapshot.DisplayPos.Y * (2.0f / displaySize.Y)),
        };
        encoder.PushConstants(input.OverlayCommandBuffer, input.Resources.PipelineLayout, ShaderStageFlags.VertexBit, in constants);

        uint width = input.Target.Extent.Width;
        uint height = input.Target.Extent.Height;
        if (width == 0 || height == 0)
            return;

        Viewport viewport = new() { Width = width, Height = height, MaxDepth = 1f };
        encoder.Runtime.Api.CmdSetViewport(input.OverlayCommandBuffer, 0, 1, &viewport);
        Vector2 scale = input.Snapshot.FramebufferScale * new Vector2(
            width / (float)input.Snapshot.FramebufferWidth,
            height / (float)input.Snapshot.FramebufferHeight);
        DescriptorSet boundSet = default;
        uint vertexOffset = 0;
        uint indexOffset = 0;
        for (int listIndex = 0; listIndex < input.Snapshot.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot list = input.Snapshot.CommandLists[listIndex];
            for (int commandIndex = 0; commandIndex < list.CommandCount; commandIndex++)
            {
                VulkanImGuiCommandSnapshot command = list.Commands[commandIndex];
                if (command.HasUserCallback || !input.DescriptorSets.TryGetValue(command.TextureId, out DescriptorSet set) || set.Handle == 0)
                    set = input.Resources.FontDescriptorSet;
                if (set.Handle == 0)
                    continue;
                if (set.Handle != boundSet.Handle)
                {
                    encoder.BindDescriptorSet(input.OverlayCommandBuffer, input.Resources.PipelineLayout, 0, set, []);
                    boundSet = set;
                }

                Vector4 clip = command.ClipRect;
                float minX = Math.Max(0f, (clip.X - input.Snapshot.DisplayPos.X) * scale.X);
                float minY = Math.Max(0f, (clip.Y - input.Snapshot.DisplayPos.Y) * scale.Y);
                float maxX = Math.Min(width, (clip.Z - input.Snapshot.DisplayPos.X) * scale.X);
                float maxY = Math.Min(height, (clip.W - input.Snapshot.DisplayPos.Y) * scale.Y);
                if (maxX <= minX || maxY <= minY)
                    continue;

                Rect2D scissor = new()
                {
                    Offset = new Offset2D((int)minX, (int)minY),
                    Extent = new Extent2D((uint)(maxX - minX), (uint)(maxY - minY)),
                };
                encoder.Runtime.Api.CmdSetScissor(input.OverlayCommandBuffer, 0, 1, &scissor);
                encoder.Runtime.Api.CmdDrawIndexed(input.OverlayCommandBuffer, command.ElemCount, 1,
                    command.IdxOffset + indexOffset, (int)(command.VtxOffset + vertexOffset), 0);
            }
            indexOffset += (uint)list.IndexCount;
            vertexOffset += (uint)list.VertexCount;
        }
    }

    private static void BeginDynamicRendering(VulkanTrackedCommandEncoder encoder, CommandBuffer commandBuffer, RenderingInfo* info, bool preferKhr)
    {
        if (!preferKhr && encoder.Runtime.DeviceContext.InstanceApiVersion >= Vk.Version13)
            encoder.Runtime.Api.CmdBeginRendering(commandBuffer, info);
        else
            (encoder.Runtime.DeviceContext.ExtensionFunctions.KhrDynamicRendering ?? throw new InvalidOperationException("VK_KHR_dynamic_rendering is unavailable."))
                .CmdBeginRendering(commandBuffer, info);
    }

    private static void EndDynamicRendering(VulkanTrackedCommandEncoder encoder, CommandBuffer commandBuffer, bool preferKhr)
    {
        if (!preferKhr && encoder.Runtime.DeviceContext.InstanceApiVersion >= Vk.Version13)
            encoder.Runtime.Api.CmdEndRendering(commandBuffer);
        else
            (encoder.Runtime.DeviceContext.ExtensionFunctions.KhrDynamicRendering ?? throw new InvalidOperationException("VK_KHR_dynamic_rendering is unavailable."))
                .CmdEndRendering(commandBuffer);
    }
}
