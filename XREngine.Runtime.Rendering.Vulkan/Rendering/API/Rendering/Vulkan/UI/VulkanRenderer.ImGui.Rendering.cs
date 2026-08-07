using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private bool CanRecordImGuiOverlayCommandBuffer(uint imageIndex)
    {
        if (RenderDiagnosticsFlags.VkSkipImGui)
        {
            Debug.VulkanEvery(
                $"Vulkan.SkipImGui.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping ImGui overlay due to XRE_SKIP_IMGUI=1.");
            return false;
        }

        if (_outputRuntime._imguiResources.OverlayCommandBuffers is null ||
            imageIndex >= _outputRuntime._imguiResources.OverlayCommandBuffers.Length ||
            OutputRuntime.Desktop.Images is null ||
            imageIndex >= OutputRuntime.Desktop.Images.Length)
        {
            return false;
        }

        bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
            OutputRuntime.Desktop.ImageViews is not null &&
            imageIndex < OutputRuntime.Desktop.ImageViews.Length;

        if (!useDynamicRendering &&
            (OutputRuntime.Desktop.Framebuffers is null ||
             imageIndex >= OutputRuntime.Desktop.Framebuffers.Length ||
             ResourceRuntime.SwapchainLoadRenderPass.Handle == 0))
        {
            return false;
        }

        CommandBuffer commandBuffer = _outputRuntime._imguiResources.OverlayCommandBuffers[imageIndex];
        return commandBuffer.Handle != 0;
    }

    private bool TryConsumeRenderableImGuiOverlaySnapshot(out VulkanImGuiFrameSnapshot? drawData)
    {
        drawData = null;

        if (RenderDiagnosticsFlags.VkSkipImGui)
        {
            Debug.VulkanEvery(
                $"Vulkan.SkipImGui.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Skipping ImGui overlay due to XRE_SKIP_IMGUI=1.");
            return false;
        }

        if (!_outputRuntime._imguiDrawData.TryConsume(out drawData) || drawData is null)
            return false;

        if (!HasRenderableImGuiSnapshot(drawData))
        {
            _outputRuntime._imguiDrawData.Discard(drawData);
            drawData = null;
            return false;
        }

        bool snapshotMatchesSwapchain =
            drawData.FramebufferWidth == OutputRuntime.Desktop.Extent.Width &&
            drawData.FramebufferHeight == OutputRuntime.Desktop.Extent.Height;
        bool canMapLiveSnapshotToScaledSwapchain =
            OutputRuntime.Desktop.PresentScalingActive &&
            XRWindow.IsInteractiveResizeInProgress;
        if (!snapshotMatchesSwapchain && !canMapLiveSnapshotToScaledSwapchain)
        {
            ResetImGuiFrameMarker();

            Debug.VulkanEvery(
                $"Vulkan.ImGui.StaleSnapshot.{GetHashCode()}",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Skipping stale ImGui overlay snapshot. Snapshot={0}x{1} Swapchain={2}x{3}.",
                drawData.FramebufferWidth,
                drawData.FramebufferHeight,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height);
            _outputRuntime._imguiDrawData.Discard(drawData);
            drawData = null;
            return false;
        }

        if (!snapshotMatchesSwapchain)
        {
            Debug.VulkanEvery(
                $"Vulkan.ImGui.ScaledSnapshot.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Mapping live ImGui snapshot to scaled-present swapchain. Snapshot={0}x{1} Swapchain={2}x{3}.",
                drawData.FramebufferWidth,
                drawData.FramebufferHeight,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height);
        }

        return true;
    }

    private bool TryRecordImGuiOverlayCommandBuffer(
        uint imageIndex,
        VulkanImGuiFrameSnapshot drawData,
        ImageLayout initialSwapchainLayout,
        CommandBuffer predecessorCommandBuffer,
        out CommandBuffer overlayCommandBuffer)
    {
        overlayCommandBuffer = default;

        if (!CanRecordImGuiOverlayCommandBuffer(imageIndex) ||
            !HasRenderableImGuiSnapshot(drawData))
        {
            return false;
        }

        EnsureImGuiFontResources();
        EnsureImGuiPipeline();

        bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
            OutputRuntime.Desktop.ImageViews is not null &&
            imageIndex < OutputRuntime.Desktop.ImageViews.Length;

        CommandBuffer commandBuffer = _outputRuntime._imguiResources.OverlayCommandBuffers![imageIndex];

        ResetVulkanCommandBufferTracked(commandBuffer);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.ImGuiOverlay");
        if (Api.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
            throw new InvalidOperationException("Failed to begin ImGui overlay command buffer.");

        ResetCommandBufferBindState(commandBuffer);
        SeedRecordedImageLayoutState(commandBuffer, predecessorCommandBuffer);
        TransitionImGuiSnapshotTexturesForSampling(commandBuffer, drawData);
        CmdBeginLabel(commandBuffer, "ImGuiOverlay");

        if (useDynamicRendering)
        {
            RecordImGuiStreamlineUi(commandBuffer, imageIndex, drawData);

            TransitionSwapchainImageForImGuiOverlay(
                commandBuffer,
                imageIndex,
                initialSwapchainLayout,
                ImageLayout.ColorAttachmentOptimal);

            RenderingAttachmentInfo colorAttachment = new()
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = OutputRuntime.Desktop.ImageViews![imageIndex],
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
            };

            RenderingInfo renderingInfo = new()
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D(0, 0),
                    Extent = OutputRuntime.Desktop.Extent
                },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
            };

            CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
            RenderImGuiSnapshot(commandBuffer, imageIndex, drawData);
            CmdEndDynamicRendering(commandBuffer);

            TransitionSwapchainImageForImGuiOverlay(
                commandBuffer,
                imageIndex,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.PresentSrcKhr);
        }
        else
        {
            RenderPassBeginInfo renderPassInfo = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = ResourceRuntime.SwapchainLoadRenderPass,
                Framebuffer = OutputRuntime.Desktop.Framebuffers![imageIndex],
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D(0, 0),
                    Extent = OutputRuntime.Desktop.Extent
                }
            };

            const uint attachmentCount = 2;
            ClearValue* clearValues = stackalloc ClearValue[(int)attachmentCount];
            ActiveState.WriteClearValues(clearValues, attachmentCount);
            renderPassInfo.ClearValueCount = attachmentCount;
            renderPassInfo.PClearValues = clearValues;

            CmdBeginRenderPassTracked(commandBuffer, &renderPassInfo, SubpassContents.Inline);
            RenderImGuiSnapshot(commandBuffer, imageIndex, drawData);
            Api.CmdEndRenderPass(commandBuffer);
        }

        CmdEndLabel(commandBuffer);

        if (EndCommandBufferTracked(commandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to end ImGui overlay command buffer.");

        overlayCommandBuffer = commandBuffer;
        return true;
    }

    /// <summary>
    /// Renders ImGui into the transparent, premultiplied-alpha surface consumed by DLSS-G.
    /// The regular swapchain overlay is still rendered below for non-generated frames.
    /// </summary>
    private void RecordImGuiStreamlineUi(
        CommandBuffer commandBuffer,
        uint imageIndex,
        VulkanImGuiFrameSnapshot drawData)
    {
        if (!TryGetStreamlineUiAttachment(
                imageIndex,
                out Image uiImage,
                out ImageView uiView,
                out ImageLayout oldLayout))
        {
            return;
        }

        TransitionStreamlineUiImage(
            commandBuffer,
            uiImage,
            oldLayout,
            ImageLayout.ColorAttachmentOptimal);

        RenderingAttachmentInfo colorAttachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = uiView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue
            {
                Color = new ClearColorValue(0f, 0f, 0f, 0f),
            },
        };

        RenderingInfo renderingInfo = new()
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = OutputRuntime.Desktop.Extent,
            },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };

        CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
        RenderImGuiSnapshot(commandBuffer, imageIndex, drawData);
        CmdEndDynamicRendering(commandBuffer);

        TransitionStreamlineUiImage(
            commandBuffer,
            uiImage,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.General);
        MarkStreamlineUiImageInitialized(imageIndex);
    }

    private static bool HasRenderableImGuiSnapshot(VulkanImGuiFrameSnapshot drawData)
        => drawData.TotalVertexCount > 0 &&
           drawData.TotalIndexCount > 0 &&
           drawData.CommandListCount > 0 &&
           drawData.DisplaySize.X > 0f &&
           drawData.DisplaySize.Y > 0f &&
           drawData.FramebufferWidth > 0 &&
           drawData.FramebufferHeight > 0;

    private void TransitionImGuiSnapshotTexturesForSampling(
        CommandBuffer commandBuffer,
        VulkanImGuiFrameSnapshot drawData)
    {
        for (int listIndex = 0; listIndex < drawData.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot commandList = drawData.CommandLists[listIndex];
            for (int commandIndex = 0; commandIndex < commandList.CommandCount; commandIndex++)
            {
                VulkanImGuiCommandSnapshot drawCommand = commandList.Commands[commandIndex];
                if (drawCommand.HasUserCallback || drawCommand.TextureId <= 1 ||
                    !_outputRuntime._imguiTextureRegistry.TexturesById.TryGetValue(drawCommand.TextureId, out XRTexture? texture) ||
                    GetOrCreateAPIRenderObject(texture, generateNow: false) is not IVkImageDescriptorSource source)
                {
                    continue;
                }

                ImageView view = ResolveImGuiDescriptorView(source);
                if (view.Handle == 0 ||
                    !TryGetDescriptorHeapImageViewCreateInfo(view, out ImageViewCreateInfo viewInfo) ||
                    viewInfo.Image.Handle == 0)
                {
                    continue;
                }

                ImageLayout descriptorLayout = ResolveDescriptorImageLayout(
                    source,
                    DescriptorType.CombinedImageSampler);
                VulkanImageAccessState priorState;
                ImageLayout oldLayout;
                if (TryGetRecordedImageAccessState(
                        commandBuffer,
                        viewInfo.Image,
                        viewInfo.SubresourceRange,
                        out priorState))
                {
                    oldLayout = priorState.Layout;
                }
                else
                {
                    oldLayout = source.TrackedImageLayout;
                    priorState = ResolveVulkanImageAccessState(
                        oldLayout,
                        viewInfo.SubresourceRange.AspectMask);
                }

                if (oldLayout == descriptorLayout)
                    continue;

                VulkanImageAccessState nextState = ResolveVulkanImageAccessState(
                    descriptorLayout,
                    viewInfo.SubresourceRange.AspectMask);
                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = (AccessFlags)(ulong)priorState.AccessMask,
                    DstAccessMask = (AccessFlags)(ulong)nextState.AccessMask,
                    OldLayout = oldLayout,
                    NewLayout = descriptorLayout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = viewInfo.Image,
                    SubresourceRange = viewInfo.SubresourceRange,
                };
                CmdPipelineBarrierTracked(
                    commandBuffer,
                    (PipelineStageFlags)(ulong)priorState.StageMask,
                    PipelineStageFlags.FragmentShaderBit,
                    DependencyFlags.None,
                    0, null, 0, null,
                    1, &barrier);
            }
        }
    }

    private void TransitionSwapchainImageForImGuiOverlay(
        CommandBuffer commandBuffer,
        uint imageIndex,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        if (OutputRuntime.Desktop.Images is null || imageIndex >= OutputRuntime.Desktop.Images.Length)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = oldLayout == ImageLayout.ColorAttachmentOptimal
                ? AccessFlags.ColorAttachmentWriteBit
                : 0,
            DstAccessMask = newLayout == ImageLayout.ColorAttachmentOptimal
                ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                : 0,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = OutputRuntime.Desktop.Images[imageIndex],
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        PipelineStageFlags srcStage = oldLayout == ImageLayout.ColorAttachmentOptimal
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.BottomOfPipeBit;
        PipelineStageFlags dstStage = newLayout == ImageLayout.ColorAttachmentOptimal
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.BottomOfPipeBit;

        CmdPipelineBarrierTracked(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void TransitionImGuiViewportImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = oldLayout == ImageLayout.ColorAttachmentOptimal
                ? AccessFlags.ColorAttachmentWriteBit
                : 0,
            DstAccessMask = newLayout == ImageLayout.ColorAttachmentOptimal
                ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                : 0,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        PipelineStageFlags srcStage = oldLayout == ImageLayout.ColorAttachmentOptimal
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.BottomOfPipeBit;
        PipelineStageFlags dstStage = newLayout == ImageLayout.ColorAttachmentOptimal
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.BottomOfPipeBit;
        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private void RenderImGuiSnapshot(CommandBuffer commandBuffer, uint imageIndex, VulkanImGuiFrameSnapshot drawData)
    {
        ulong vertexBytes = (ulong)(drawData.TotalVertexCount * sizeof(ImDrawVert));
        ulong indexBytes = (ulong)(drawData.TotalIndexCount * sizeof(ushort));
        int bufferSlot = EnsureImGuiDrawBuffers(imageIndex, vertexBytes, indexBytes);
        ref VulkanImGuiDrawBufferSet buffers = ref _outputRuntime._imguiResources.DrawBuffers[bufferSlot];
        RenderImGuiSnapshot(commandBuffer, drawData, OutputRuntime.Desktop.Extent, ref buffers);
    }

    private void RenderImGuiViewportSnapshot(
        CommandBuffer commandBuffer,
        uint imageIndex,
        VulkanImGuiFrameSnapshot drawData,
        Extent2D rasterExtent,
        ref VulkanImGuiDrawBufferSet[] drawBuffers)
    {
        int bufferSlot = EnsureImGuiDrawBufferSlot(
            ref drawBuffers,
            imageIndex,
            checked((int)imageIndex + 1));
        ref VulkanImGuiDrawBufferSet buffers = ref drawBuffers[bufferSlot];
        EnsureImGuiDrawBuffers(
            ref buffers,
            (ulong)(drawData.TotalVertexCount * sizeof(ImDrawVert)),
            (ulong)(drawData.TotalIndexCount * sizeof(ushort)));
        RenderImGuiSnapshot(commandBuffer, drawData, rasterExtent, ref buffers);
    }

    private void RenderImGuiSnapshot(
        CommandBuffer commandBuffer,
        VulkanImGuiFrameSnapshot drawData,
        Extent2D rasterExtent,
        ref VulkanImGuiDrawBufferSet buffers)
    {
        ulong vertexBytes = (ulong)(drawData.TotalVertexCount * sizeof(ImDrawVert));
        ulong indexBytes = (ulong)(drawData.TotalIndexCount * sizeof(ushort));

        void* mappedVertex = null;
        void* mappedIndex = null;

        if (!TryMapBufferMemory(buffers.VertexBuffer, buffers.VertexBufferMemory, 0, vertexBytes, out mappedVertex))
            throw new InvalidOperationException("Failed to map ImGui vertex buffer.");

        if (!TryMapBufferMemory(buffers.IndexBuffer, buffers.IndexBufferMemory, 0, indexBytes, out mappedIndex))
        {
            UnmapBufferMemory(buffers.VertexBuffer, buffers.VertexBufferMemory);
            throw new InvalidOperationException("Failed to map ImGui index buffer.");
        }

        try
        {
            CopyImGuiSnapshot(drawData, mappedVertex, mappedIndex);
        }
        finally
        {
            UnmapBufferMemory(buffers.IndexBuffer, buffers.IndexBufferMemory);
            UnmapBufferMemory(buffers.VertexBuffer, buffers.VertexBufferMemory);
        }

        BindPipelineTracked(commandBuffer, PipelineBindPoint.Graphics, _outputRuntime._imguiResources.Pipeline);

        DescriptorSet boundDescriptorSet = default;
        bool hasBoundDescriptorSet = false;
        DescriptorHeapPushDataPayload? boundDescriptorHeapPayload = null;

        Buffer vertexBuffer = buffers.VertexBuffer;
        ulong vertexOffset = 0;
        BindVertexBufferTracked(commandBuffer, 0, vertexBuffer, vertexOffset);
        BindIndexBufferTracked(commandBuffer, buffers.IndexBuffer, 0, IndexType.Uint16);

        Vector2 clipOff = drawData.DisplayPos;
        Vector2 displaySize = drawData.DisplaySize;

        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            return;

        VulkanImGuiPushConstants pushConstants = new()
        {
            Scale = new Vector2(2.0f / displaySize.X, 2.0f / displaySize.Y),
            Translate = new Vector2(
                -1.0f - clipOff.X * (2.0f / displaySize.X),
                -1.0f - clipOff.Y * (2.0f / displaySize.Y))
        };

        PushConstantsTracked(commandBuffer, _outputRuntime._imguiResources.PipelineLayout, ShaderStageFlags.VertexBit, 0, pushConstants);

        uint fbWidth = rasterExtent.Width;
        uint fbHeight = rasterExtent.Height;
        if (fbWidth == 0 || fbHeight == 0)
            return;

        Vector2 snapshotToRasterScale = new(
            fbWidth / (float)drawData.FramebufferWidth,
            fbHeight / (float)drawData.FramebufferHeight);
        Vector2 clipScale = drawData.FramebufferScale * snapshotToRasterScale;

        Viewport imguiViewport = CreateImGuiViewport(fbWidth, fbHeight);
        Api.CmdSetViewport(commandBuffer, 0, 1, &imguiViewport);

        uint globalVtxOffset = 0;
        uint globalIdxOffset = 0;

        for (int listIndex = 0; listIndex < drawData.CommandListCount; listIndex++)
        {
            VulkanImGuiCommandListSnapshot cmdList = drawData.CommandLists[listIndex];

            for (int cmdIndex = 0; cmdIndex < cmdList.CommandCount; cmdIndex++)
            {
                VulkanImGuiCommandSnapshot drawCmd = cmdList.Commands[cmdIndex];
                if (drawCmd.HasUserCallback)
                    continue;

                if (IsDescriptorHeapDrawBindingActive)
                {
                    DescriptorHeapPushDataPayload? payload = ResolveImGuiDescriptorHeapPayload(drawCmd.TextureId);
                    if (payload is null)
                    {
                        Debug.VulkanWarning("[Vulkan.ImGui] Skipping draw because descriptor heap payload is missing for textureId={0}.", drawCmd.TextureId);
                        continue;
                    }

                    if (!ReferenceEquals(payload, boundDescriptorHeapPayload))
                    {
                        fixed (uint* data = payload.Dwords)
                        {
                            if (!TryPushDescriptorHeapData(commandBuffer, 0, data, (uint)(payload.Dwords.Length * sizeof(uint)), out string reason))
                            {
                                Debug.VulkanWarning("[Vulkan.ImGui] Skipping draw because descriptor heap push failed for textureId={0}: {1}", drawCmd.TextureId, reason);
                                continue;
                            }
                        }

                        boundDescriptorHeapPayload = payload;
                    }
                }
                else
                {
                    DescriptorSet drawDescriptorSet = ResolveImGuiDescriptorSet(drawCmd.TextureId);
                    if (!hasBoundDescriptorSet || drawDescriptorSet.Handle != boundDescriptorSet.Handle)
                    {
                        DescriptorSet setToBind = drawDescriptorSet;
                        BindDescriptorSetTracked(
                            commandBuffer,
                            PipelineBindPoint.Graphics,
                            _outputRuntime._imguiResources.PipelineLayout,
                            0,
                            setToBind);
                        boundDescriptorSet = drawDescriptorSet;
                        hasBoundDescriptorSet = true;
                    }
                }

                Vector4 clipRect = drawCmd.ClipRect;
                float clipMinX = (clipRect.X - clipOff.X) * clipScale.X;
                float clipMinY = (clipRect.Y - clipOff.Y) * clipScale.Y;
                float clipMaxX = (clipRect.Z - clipOff.X) * clipScale.X;
                float clipMaxY = (clipRect.W - clipOff.Y) * clipScale.Y;

                if (clipMinX < 0f) clipMinX = 0f;
                if (clipMinY < 0f) clipMinY = 0f;
                if (clipMaxX > fbWidth) clipMaxX = fbWidth;
                if (clipMaxY > fbHeight) clipMaxY = fbHeight;

                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
                    continue;

                Rect2D scissor = new()
                {
                    Offset = new Offset2D((int)clipMinX, (int)clipMinY),
                    Extent = new Extent2D((uint)(clipMaxX - clipMinX), (uint)(clipMaxY - clipMinY))
                };
                Api.CmdSetScissor(commandBuffer, 0, 1, &scissor);

                Api.CmdDrawIndexed(
                    commandBuffer,
                    drawCmd.ElemCount,
                    1,
                    drawCmd.IdxOffset + globalIdxOffset,
                    (int)(drawCmd.VtxOffset + globalVtxOffset),
                    0);
            }

            globalIdxOffset += (uint)cmdList.IndexCount;
            globalVtxOffset += (uint)cmdList.VertexCount;
        }
    }

    private static Viewport CreateImGuiViewport(uint framebufferWidth, uint framebufferHeight)
        => new()
        {
            X = 0f,
            Y = 0f,
            Width = framebufferWidth,
            Height = framebufferHeight,
            MinDepth = 0f,
            MaxDepth = 1f
        };

    private bool ShouldEmulateOpenGlImGuiSrgbPassthrough()
        => IsSrgbSwapchainFormat(OutputRuntime.Desktop.ImageFormat) || IsLinearSrgbSwapchainColorSpace(OutputRuntime.Desktop.ImageColorSpace);

    private static bool IsSrgbSwapchainFormat(Format format)
        => format is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb;

    private static bool IsLinearSrgbSwapchainColorSpace(ColorSpaceKHR colorSpace)
        => colorSpace is ColorSpaceKHR.SpaceExtendedSrgbLinearExt;

}
