using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private bool TryEnsureMutableDynamicUiSecondaryCommandBuffer(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            out CommandBuffer secondaryCommandBuffer)
        {
            secondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
            if (secondaryCommandBuffer.Handle != 0 &&
                CanResetVulkanCommandBuffer(secondaryCommandBuffer, out _))
            {
                return true;
            }

            CommandPool pool = variant.DynamicUiSecondaryCommandPool;
            if (pool.Handle == 0)
                return false;

            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Secondary,
                CommandBufferCount = 1,
            };
            Result allocateResult = AllocateVulkanCommandBuffersTracked(
                ref allocateInfo,
                out CommandBuffer replacement,
                "DynamicUiText.SecondaryReplacement");
            if (allocateResult != Result.Success || replacement.Handle == 0)
                return false;

            CommandBuffer previous = secondaryCommandBuffer;
            if (previous.Handle != 0 && variant.OwnsDynamicUiSecondaryCommandBuffer)
                DeferSecondaryCommandBufferFree(imageIndex, pool, previous);

            variant.DynamicUiSecondaryCommandBuffer = replacement;
            variant.OwnsDynamicUiSecondaryCommandBuffer = true;
            variant.DynamicUiSecondaryRecorded = false;
            RegisterCommandBufferImageIndex(replacement, imageIndex);
            SetDebugObjectName(
                ObjectType.CommandBuffer,
                unchecked((ulong)replacement.Handle),
                $"DynamicUiText.SecondaryReplacement[{imageIndex}]");
            secondaryCommandBuffer = replacement;

            Debug.VulkanEvery(
                $"Vulkan.DynamicUiText.SecondaryCopyOnWrite.{GetHashCode()}.{imageIndex}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Replaced immutable dynamic UI secondary. image={0} old=0x{1:X} new=0x{2:X}",
                imageIndex,
                previous.Handle,
                replacement.Handle);
            return true;
        }

        private bool RecordDynamicUiBatchTextSecondaryCommandBuffer(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            FrameOp[] dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            bool forceRecord = false,
            bool includeDepthAttachment = true)
        {
            if (dynamicUiBatchTextOps.Length == 0)
            {
                variant.DynamicUiOpCount = 0;
                variant.DynamicUiSignature = 0;
                variant.DynamicUiSecondaryRecorded = false;
                return true;
            }

            if (!forceRecord &&
                variant.DynamicUiSignature == dynamicUiBatchTextSignature &&
                variant.DynamicUiSecondaryRecorded &&
                variant.DynamicUiSecondaryIncludesDepth == includeDepthAttachment)
            {
                if (XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
                    XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.DynamicUiText.SecondaryReuse.{GetHashCode()}.{imageIndex}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Reusing dynamic UI text secondary. image={0} ops={1} signature=0x{2:X}",
                        imageIndex,
                        variant.DynamicUiOpCount,
                        dynamicUiBatchTextSignature);
                }
                return true;
            }

            if (!TryEnsureMutableDynamicUiSecondaryCommandBuffer(
                    imageIndex,
                    variant,
                    out CommandBuffer secondaryCommandBuffer))
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "dynamic-ui-text",
                    null,
                    dynamicUiBatchTextOps[0].PassIndex,
                    "a mutable secondary command buffer could not be allocated");
                variant.DynamicUiSecondaryRecorded = false;
                return false;
            }

            bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                swapChainImageViews is not null &&
                swapChainImages is not null &&
                imageIndex < swapChainImageViews.Length &&
                imageIndex < swapChainImages.Length;

            RenderPass inheritedRenderPass = useDynamicRendering ? default : _renderPassLoad;
            Framebuffer inheritedFramebuffer = default;
            if (!useDynamicRendering && swapChainFramebuffers is not null && imageIndex < swapChainFramebuffers.Length)
                inheritedFramebuffer = swapChainFramebuffers[imageIndex];

            if (!useDynamicRendering && (inheritedRenderPass.Handle == 0 || inheritedFramebuffer.Handle == 0))
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "dynamic-ui-text",
                    null,
                    dynamicUiBatchTextOps[0].PassIndex,
                    $"legacy swapchain inheritance unavailable renderPass=0x{inheritedRenderPass.Handle:X} framebuffer=0x{inheritedFramebuffer.Handle:X}");
                variant.DynamicUiSecondaryRecorded = false;
                return false;
            }

            CommandBufferInheritanceInfo inheritanceInfo = new()
            {
                SType = StructureType.CommandBufferInheritanceInfo,
                RenderPass = inheritedRenderPass,
                Subpass = 0,
                Framebuffer = inheritedFramebuffer,
                OcclusionQueryEnable = Vk.False,
                QueryFlags = QueryControlFlags.None,
                PipelineStatistics = QueryPipelineStatisticFlags.None
            };

            Format* colorAttachmentFormats = stackalloc Format[1];
            colorAttachmentFormats[0] = swapChainImageFormat;

            DynamicRenderingFormatSignature dynamicRenderingFormats = useDynamicRendering
                ? includeDepthAttachment
                    ? CreateSwapchainDynamicRenderingFormatSignature(swapChainImageFormat, _swapchainDepthFormat)
                    : CreateSwapchainColorOnlyDynamicRenderingFormatSignature(swapChainImageFormat)
                : default;

            CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = new()
            {
                SType = StructureType.CommandBufferInheritanceRenderingInfo,
                Flags = 0,
                ViewMask = dynamicRenderingFormats.ViewMask,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = colorAttachmentFormats,
                DepthAttachmentFormat = dynamicRenderingFormats.DepthAttachmentFormat,
                StencilAttachmentFormat = dynamicRenderingFormats.StencilAttachmentFormat,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            if (useDynamicRendering)
            {
                DynamicRenderingLocalReadPlan localReadInheritance = default;
                void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                TryAppendDynamicRenderingLocalReadPNext(
                    in localReadInheritance,
                    dynamicRenderingFormats.ColorAttachmentCount,
                    ref localReadInheritancePNext,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null);
                renderingInheritanceInfo.PNext = localReadInheritancePNext;
                inheritanceInfo.PNext = &renderingInheritanceInfo;
            }

            CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
            BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
            BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
            TryAppendDescriptorHeapInheritancePNext(
                ref inheritanceInfo,
                &descriptorHeapInheritanceInfo,
                &inheritedSamplerHeapInfo,
                &inheritedResourceHeapInfo);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit,
                PInheritanceInfo = &inheritanceInfo,
            };

            CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = recordingScratch.DynamicUiMeshDrawSlotsByRenderer;
            meshDrawSlotsByRenderer.Clear();
            meshDrawSlotsByRenderer.EnsureCapacity(recordingScratch.DynamicUiMeshDrawSlotCapacityHint);
            if (!TryRegisterFrameWideMeshFrameDataRequirements(
                    Array.Empty<FrameOp>(),
                    dynamicUiBatchTextOps,
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    sealAfterRegister: true,
                    meshDrawSlotsByRenderer,
                    recordingScratch,
                    recordingScratch.DynamicUiMeshFrameDataFamilyBases,
                    out _,
                    out string frameWideReason))
            {
                throw new InvalidOperationException(
                    $"Frame-wide mesh frame-data manifest rejected dynamic-UI recording: {frameWideReason}");
            }

            VulkanMeshFrameDataReservationManifest frameDataManifest =
                recordingScratch.MeshFrameDataManifest;
            frameDataManifest.Begin(MeshFrameDataReservationGeneration, recordingScratch.DynamicUiMeshDrawSlotCapacityHint);
            foreach (KeyValuePair<VkMeshRenderer, int> reservation in meshDrawSlotsByRenderer)
            {
                if (frameDataManifest.TryReserve(reservation.Key, reservation.Value))
                    continue;
                frameDataManifest.End();
                throw new InvalidOperationException(
                    $"Unable to reserve {reservation.Value} dynamic-UI mesh frame-data slots before secondary recording.");
            }

            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily =
                recordingScratch.DynamicUiMeshDrawSlotsByRendererFamily;
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases =
                recordingScratch.DynamicUiMeshFrameDataFamilyBases;
            meshDrawSlotsByRendererFamily.Clear();
            bool graphicsPipelinesReady = true;
            string firstGraphicsPipelinePendingReason = string.Empty;
            for (int i = 0; i < dynamicUiBatchTextOps.Length; i++)
            {
                if (dynamicUiBatchTextOps[i] is not MeshDrawOp drawOp)
                    continue;
                int drawSlot = GetFrameWideMeshDrawUniformSlot(
                    meshDrawSlotsByRendererFamily,
                    meshFrameDataFamilyBases,
                    drawOp.Draw.Renderer,
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    EVulkanMeshFrameDataStreamKind.DynamicUi,
                    drawOp.Context,
                    drawOp.Draw);
                using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                    drawOp.Context.PipelineInstance);
                using var plannerScope =
                    EnterFrameOpResourcePlannerReadbackScope(drawOp.Context);
                int descriptorFrameIndex = imageIndex > int.MaxValue ? int.MaxValue : (int)imageIndex;
                if (!drawOp.Draw.Renderer.TryPrewarmFrameDataForRecording(
                        drawOp.Draw,
                        drawSlot,
                        descriptorFrameIndex,
                        out string reason))
                {
                    frameDataManifest.End();
                    throw new InvalidOperationException(
                        $"Dynamic-UI frame-data reservation failed before secondary recording at slot {drawSlot}: {reason}");
                }

                int pipelinePassIndex = EnsureValidPassIndex(
                    drawOp.PassIndex,
                    drawOp.GetType().Name,
                    drawOp.Context.PassMetadata);
                if (pipelinePassIndex == int.MinValue ||
                    drawOp.Draw.Renderer.TryPrewarmGraphicsPipelinesForRecording(
                        drawOp.Draw,
                        inheritedRenderPass,
                        useDynamicRendering,
                        dynamicRenderingFormats,
                        pipelinePassIndex,
                        drawOp.Context.PassMetadata,
                        depthStencilReadOnly: false,
                        drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                        out string pipelineReason))
                {
                    continue;
                }

                graphicsPipelinesReady = false;
                if (firstGraphicsPipelinePendingReason.Length == 0)
                {
                    firstGraphicsPipelinePendingReason =
                        $"op={i} mesh='{drawOp.Draw.Renderer.Mesh?.Name ?? "<unnamed mesh>"}': {pipelineReason}";
                }
            }
            meshDrawSlotsByRendererFamily.Clear();

            if (!graphicsPipelinesReady)
            {
                frameDataManifest.End();
                variant.DynamicUiSecondaryRecorded = false;
                Debug.VulkanWarningEvery(
                    $"Vulkan.DynamicUi.PipelinePrewarmPending.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Dynamic-UI secondary recording deferred before vkBeginCommandBuffer because required graphics pipelines are pending. detail={0}",
                    firstGraphicsPipelinePendingReason);
                return false;
            }

            if (!frameDataManifest.TrySeal(MeshFrameDataReservationGeneration, MeshFrameDataReservedBytes))
            {
                frameDataManifest.End();
                throw new InvalidOperationException(
                    "Mesh frame-data generation changed while the dynamic-UI reservation manifest was being materialized.");
            }
            using VulkanMeshFrameDataManifestRecordingScope frameDataManifestScope = new(frameDataManifest);

            // Pipeline/materialization deferral must not reset the last executable secondary.
            // A cached primary may still reference it until that primary is safely re-recorded.
            variant.DynamicUiSecondaryRecorded = false;
            Result resetResult = ResetVulkanCommandBufferTracked(secondaryCommandBuffer);
            if (resetResult != Result.Success)
                throw new InvalidOperationException(
                    $"Failed to reset dynamic UI text secondary command buffer: {resetResult}.");

            bool recordingStarted = false;
            int recordedDrawCount = 0;
            try
            {
                if (Api!.BeginCommandBuffer(secondaryCommandBuffer, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin dynamic UI text secondary command buffer.");

                ResetCommandBufferBindState(secondaryCommandBuffer);
                recordingStarted = true;
                meshDrawSlotsByRendererFamily.Clear();

                for (int i = 0; i < dynamicUiBatchTextOps.Length; i++)
                {
                    if (dynamicUiBatchTextOps[i] is not MeshDrawOp drawOp)
                        continue;

                    int opPassIndex = EnsureValidPassIndex(drawOp.PassIndex, drawOp.GetType().Name, drawOp.Context.PassMetadata);
                    if (opPassIndex == int.MinValue)
                        continue;

                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);

                    Viewport viewport = drawOp.Draw.Viewport;
                    Rect2D scissor = drawOp.Draw.Scissor;
                    uint viewportScissorCount = drawOp.Draw.ViewportScissorCount;
                    if (viewportScissorCount > 1 &&
                        drawOp.Draw.IndexedViewports is { } indexedViewports &&
                        drawOp.Draw.IndexedScissors is { } indexedScissors &&
                        indexedViewports.Length >= (int)viewportScissorCount &&
                        indexedScissors.Length >= (int)viewportScissorCount)
                    {
                        SetViewportScissorTracked(secondaryCommandBuffer, indexedViewports, indexedScissors, viewportScissorCount);
                    }
                    else
                    {
                        SetViewportScissorTracked(secondaryCommandBuffer, viewport, scissor);
                    }

                    int drawUniformSlot = GetFrameWideMeshDrawUniformSlot(
                        meshDrawSlotsByRendererFamily,
                        meshFrameDataFamilyBases,
                        drawOp.Draw.Renderer,
                        unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                        EVulkanMeshFrameDataStreamKind.DynamicUi,
                        drawOp.Context,
                        drawOp.Draw);
                    bool recordedDraw = drawOp.Draw.Renderer.RecordDraw(
                        secondaryCommandBuffer,
                        drawOp.Draw,
                        inheritedRenderPass,
                        useDynamicRendering,
                        dynamicRenderingFormats,
                        opPassIndex,
                        drawOp.Context.PassMetadata,
                        depthStencilReadOnly: false,
                        drawOp.Context.PipelineInstance?.DebugName ?? "<no pipeline>",
                        drawOp.Target?.Name ?? "<swapchain>",
                        drawUniformSlot,
                        unchecked((int)Math.Min(imageIndex, int.MaxValue)));
                    if (recordedDraw)
                    {
                        recordedDrawCount++;
                        if (XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
                            XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw)
                        {
                            Debug.VulkanEvery(
                                $"Vulkan.DynamicUiText.DrawRecorded.{drawOp.Draw.Renderer.GetHashCode()}",
                                TimeSpan.FromSeconds(1),
                                "[Vulkan] Dynamic UI text draw recorded. image={0} pass={1} mesh='{2}' slot={3} colors={4} depth={5} viewport=({6},{7},{8},{9}) scissor=({10},{11},{12},{13}) instances={14}",
                                imageIndex,
                                opPassIndex,
                                drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                                drawUniformSlot,
                                dynamicRenderingFormats.DescribeColorFormats(),
                                dynamicRenderingFormats.DepthAttachmentFormat,
                                drawOp.Draw.Viewport.X,
                                drawOp.Draw.Viewport.Y,
                                drawOp.Draw.Viewport.Width,
                                drawOp.Draw.Viewport.Height,
                                drawOp.Draw.Scissor.Offset.X,
                                drawOp.Draw.Scissor.Offset.Y,
                                drawOp.Draw.Scissor.Extent.Width,
                                drawOp.Draw.Scissor.Extent.Height,
                                drawOp.Draw.Instances);
                        }
                    }
                    else
                    {
                        Debug.VulkanWarningEvery(
                            $"Vulkan.DynamicUiText.DrawNotRecorded.{drawOp.Draw.Renderer.GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Dynamic UI text draw emitted no commands. pass={0} mesh='{1}' material='{2}' reason={3}",
                            opPassIndex,
                            drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                            (drawOp.Draw.MaterialOverride ?? drawOp.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                            drawOp.Draw.Renderer.DescribeReusableCommandBufferFrameDataBlocker(
                                drawOp.Draw,
                                drawUniformSlot));
                    }
                }

                if (EndCommandBufferTracked(secondaryCommandBuffer) != Result.Success)
                    throw new Exception("Failed to end dynamic UI text secondary command buffer.");
                recordingStarted = false;
            }
            catch
            {
                if (recordingStarted)
                    TryAbandonCommandBufferRecording(secondaryCommandBuffer);
                throw;
            }

            if (recordedDrawCount == 0)
            {
                variant.DynamicUiOpCount = 0;
                variant.DynamicUiSignature = 0;
                variant.DynamicUiSecondaryRecorded = false;
                return false;
            }

            variant.DynamicUiOpCount = dynamicUiBatchTextOps.Length;
            variant.DynamicUiSignature = dynamicUiBatchTextSignature;
            variant.DynamicUiSecondaryRecorded = true;
            variant.DynamicUiSecondaryIncludesDepth = includeDepthAttachment;
            if (CommandChainsEnabledForCurrentRecording)
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: 1);

            recordingScratch.DynamicUiMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
            return true;
        }

        private bool TryRecordDynamicUiBatchTextOverlayCommandBuffer(
            uint imageIndex,
            CommandBuffer secondaryCommandBuffer,
            int dynamicUiBatchTextOpCount,
            ImageLayout initialSwapchainLayout,
            CommandBuffer predecessorCommandBuffer,
            CommandBufferCacheVariant? dynamicUiBatchTextVariant,
            FrameOp[] dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            out CommandBuffer overlayCommandBuffer)
        {
            overlayCommandBuffer = default;

            if (_dynamicUiBatchTextOverlayCommandBuffers is null ||
                imageIndex >= _dynamicUiBatchTextOverlayCommandBuffers.Length)
            {
                return false;
            }

            bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                swapChainImageViews is not null &&
                imageIndex < swapChainImageViews.Length;
            if (!useDynamicRendering)
                return false;

            // The previous overlay primary owns the recorded reference that makes
            // its dynamic-text secondary immutable. Release that reference before
            // trying to reset the secondary; otherwise every frame takes the
            // copy-on-write path and leaks one replacement until the scene primary
            // happens to be re-recorded.
            CommandBuffer commandBuffer = _dynamicUiBatchTextOverlayCommandBuffers[imageIndex];
            Result resetResult = ResetVulkanCommandBufferTracked(commandBuffer);
            if (resetResult != Result.Success)
                throw new InvalidOperationException(
                    $"Failed to reset dynamic UI text overlay command buffer: {resetResult}.");
            ReleaseDeferredSecondaryCommandBuffers(imageIndex);
            if (dynamicUiBatchTextVariant is not null)
            {
                if (dynamicUiBatchTextOps.Length == 0 ||
                    !RecordDynamicUiBatchTextSecondaryCommandBuffer(
                        imageIndex,
                        dynamicUiBatchTextVariant,
                        dynamicUiBatchTextOps,
                        dynamicUiBatchTextSignature,
                        forceRecord: true,
                        includeDepthAttachment: false))
                {
                    return false;
                }

                secondaryCommandBuffer = dynamicUiBatchTextVariant.DynamicUiSecondaryCommandBuffer;
                dynamicUiBatchTextOpCount = dynamicUiBatchTextVariant.DynamicUiOpCount;
            }

            if (dynamicUiBatchTextOpCount <= 0 ||
                secondaryCommandBuffer.Handle == 0)
            {
                return false;
            }

            // Starting the engine tracking batch before the deferred-secondary
            // checks above made a normal early return look like an abandoned
            // native recording. The following frame then rejected the reset and
            // unnecessarily rebuilt the swapchain. Begin tracking only when the
            // primary will actually begin, and unwind it on every exceptional exit.
            bool trackingStarted = false;
            try
            {
                ResetCommandBufferBindState(commandBuffer);
                trackingStarted = true;

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };

                if (Api.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                    throw new InvalidOperationException("Failed to begin dynamic UI text overlay command buffer.");

                SeedRecordedImageLayoutState(commandBuffer, predecessorCommandBuffer);
                TransitionSecondaryDescriptorImagesForExecution(commandBuffer, secondaryCommandBuffer);
                CmdBeginLabel(commandBuffer, "DynamicUIBatchTextOverlay");

                RecordDynamicUiBatchTextStreamlineUi(
                    commandBuffer,
                    imageIndex,
                    secondaryCommandBuffer);

                TransitionSwapchainImageForImGuiOverlay(
                    commandBuffer,
                    imageIndex,
                    initialSwapchainLayout,
                    ImageLayout.ColorAttachmentOptimal);

                RenderingAttachmentInfo colorAttachment = new()
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = swapChainImageViews![imageIndex],
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
                        Extent = swapChainExtent
                    },
                    LayerCount = 1,
                    ColorAttachmentCount = 1,
                    PColorAttachments = &colorAttachment,
                    PDepthAttachment = null,
                    PStencilAttachment = null,
                };

                CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
                CmdExecuteCommandsTracked(commandBuffer, 1, &secondaryCommandBuffer);
                CmdEndDynamicRendering(commandBuffer);

                TransitionSwapchainImageForImGuiOverlay(
                    commandBuffer,
                    imageIndex,
                    ImageLayout.ColorAttachmentOptimal,
                    ImageLayout.PresentSrcKhr);

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.DynamicUiText.LateOverlay.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Recorded dynamic UI text late overlay after ImGui. image={0} ops={1}",
                        imageIndex,
                        dynamicUiBatchTextOpCount);
                }

                CmdEndLabel(commandBuffer);

                if (EndCommandBufferTracked(commandBuffer) != Result.Success)
                    throw new InvalidOperationException("Failed to end dynamic UI text overlay command buffer.");
                trackingStarted = false;
            }
            catch
            {
                if (trackingStarted)
                    TryAbandonCommandBufferRecording(commandBuffer);
                throw;
            }

            overlayCommandBuffer = commandBuffer;
            return true;
        }

        /// <summary>
        /// Adds native dynamic text to the same premultiplied UI surface used for
        /// DLSS-G UI recomposition. ImGui has already cleared and populated it.
        /// </summary>
        private void RecordDynamicUiBatchTextStreamlineUi(
            CommandBuffer commandBuffer,
            uint imageIndex,
            CommandBuffer secondaryCommandBuffer)
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
                    Extent = swapChainExtent,
                },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
            };

            CmdBeginDynamicRendering(commandBuffer, &renderingInfo);
            CmdExecuteCommandsTracked(commandBuffer, 1, &secondaryCommandBuffer);
            CmdEndDynamicRendering(commandBuffer);

            TransitionStreamlineUiImage(
                commandBuffer,
                uiImage,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.General);
            MarkStreamlineUiImageInitialized(imageIndex);
        }

        internal static ulong ComputeCommandChainUniformSlotSignature(
            int[] uniformSlots,
            int startIndex,
            int count)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(count);
            for (int i = 0; i < count; i++)
                hash.Add(uniformSlots[startIndex + i]);
            return hash.ToHash();
        }

        private void RecordScheduledMeshCommandChainWorker(
            CommandChainRecordingBatch batch,
            int chainIndex)
        {
            using PreparedCommandChainEncodingScope encodingScope =
                EnterPreparedCommandChainEncodingScope();
            CommandChain chain = batch.Chains[chainIndex];
            ref readonly VulkanPreparedCommandChain preparedChain =
                ref batch.PreparedFrame.GetCommandChain(chainIndex);
            if (!preparedChain.Matches(chain))
            {
                throw new InvalidOperationException(
                    $"Prepared Vulkan command-chain input became stale before encoding. " +
                    $"key={preparedChain.Key} source={preparedChain.SourceStartIndex}+" +
                    $"{preparedChain.SourceCount} artifactGeneration=" +
                    $"{preparedChain.WritableArtifact.ArtifactGeneration}.");
            }

            VulkanRecordedCommandInheritance inheritance =
                preparedChain.Inheritance;
            using VulkanWorkerSecondaryCommandArena.RecordingLease arenaLease =
                VulkanWorkerSecondaryCommandArena.EnterRecording(
                    chain.RecordedArtifact.WorkerArenaOwner);
            CommandBuffer secondary = batch.SecondaryBuffers[chainIndex];
            MarkCommandChainSecondaryCommandBufferInvalid(chain);
            Result resetResult = ResetVulkanCommandBufferTracked(secondary);
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanWorkerSecondaryCommandBufferReset();
            if (resetResult != Result.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to reset Vulkan worker mesh command-chain secondary command buffer: {resetResult}.");
            }

            CommandBufferInheritanceInfo inheritanceInfo = new()
            {
                SType = StructureType.CommandBufferInheritanceInfo,
                RenderPass = inheritance.DynamicRendering
                    ? default
                    : inheritance.RenderPass,
                Subpass = 0,
                Framebuffer = inheritance.DynamicRendering
                    ? default
                    : inheritance.Framebuffer,
                OcclusionQueryEnable = Vk.False,
                QueryFlags = QueryControlFlags.None,
                PipelineStatistics = QueryPipelineStatisticFlags.None,
            };

            uint colorAttachmentCount =
                inheritance.DynamicRenderingFormats.ColorAttachmentCount;
            Format* colorAttachmentFormats = stackalloc Format[(int)Math.Max(colorAttachmentCount, 1u)];
            CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = default;
            if (inheritance.DynamicRendering)
            {
                inheritance.DynamicRenderingFormats.CopyColorAttachmentFormats(
                    colorAttachmentFormats,
                    colorAttachmentCount);
                renderingInheritanceInfo = new CommandBufferInheritanceRenderingInfo
                {
                    SType = StructureType.CommandBufferInheritanceRenderingInfo,
                    Flags = inheritance.RenderingFlags,
                    ViewMask =
                        inheritance.DynamicRenderingFormats.ViewMask,
                    ColorAttachmentCount = colorAttachmentCount,
                    PColorAttachmentFormats = colorAttachmentCount > 0 ? colorAttachmentFormats : null,
                    DepthAttachmentFormat =
                        inheritance.DynamicRenderingFormats.DepthAttachmentFormat,
                    StencilAttachmentFormat =
                        inheritance.DynamicRenderingFormats.StencilAttachmentFormat,
                    RasterizationSamples = inheritance.Samples,
                };

                RenderingAttachmentLocationInfo localReadAttachmentLocations = default;
                RenderingInputAttachmentIndexInfo localReadInputIndices = default;
                uint* colorAttachmentLocations = stackalloc uint[(int)Math.Max(colorAttachmentCount, 1u)];
                uint* colorInputAttachmentIndices = stackalloc uint[(int)Math.Max(colorAttachmentCount, 1u)];
                uint* depthInputAttachmentIndex = stackalloc uint[1];
                uint* stencilInputAttachmentIndex = stackalloc uint[1];
                void* localReadInheritancePNext = renderingInheritanceInfo.PNext;
                DynamicRenderingLocalReadSignature localReadSignature =
                    inheritance.LocalReadSignature;
                TryAppendDynamicRenderingLocalReadInheritancePNext(
                    in localReadSignature,
                    colorAttachmentCount,
                    ref localReadInheritancePNext,
                    &localReadAttachmentLocations,
                    &localReadInputIndices,
                    colorAttachmentLocations,
                    colorInputAttachmentIndices,
                    depthInputAttachmentIndex,
                    stencilInputAttachmentIndex);
                renderingInheritanceInfo.PNext = localReadInheritancePNext;
                inheritanceInfo.PNext = &renderingInheritanceInfo;
            }

            CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
            BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
            BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
            TryAppendDescriptorHeapInheritancePNext(
                ref inheritanceInfo,
                &descriptorHeapInheritanceInfo,
                &inheritedSamplerHeapInfo,
                &inheritedResourceHeapInfo);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit,
                PInheritanceInfo = &inheritanceInfo,
            };

            // Graphics pipeline materialization and descriptor transitions are owned by the
            // render thread. Workers consume only immutable prepared draw state and have
            // no planner scope to inspect or publish.

            if (Api.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                throw new InvalidOperationException("Failed to begin Vulkan worker mesh command-chain secondary command buffer.");

            ResetCommandBufferBindState(secondary);
            MarkCommandChainSecondaryRecording(chain, secondary);
            for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)
            {
                ref readonly VkPreparedMeshDraw preparedDraw =
                    ref GetPreparedCommandChainDraw(
                        batch,
                        chainIndex,
                        drawIndex);
                int opIndex = preparedDraw.SourceOpIndex;

                Viewport viewport = preparedDraw.Viewport;
                Rect2D scissor = preparedDraw.Scissor;
                uint viewportScissorCount = preparedDraw.ViewportScissorCount;
                if (viewportScissorCount > 1 &&
                    preparedDraw.IndexedViewports is { } indexedViewports &&
                    preparedDraw.IndexedScissors is { } indexedScissors &&
                    indexedViewports.Length >= (int)viewportScissorCount &&
                    indexedScissors.Length >= (int)viewportScissorCount)
                {
                    SetViewportScissorTracked(secondary, indexedViewports, indexedScissors, viewportScissorCount);
                }
                else
                {
                    SetViewportScissorTracked(secondary, viewport, scissor);
                }

                int uniformSlot = preparedDraw.UniformSlot;
                bool recorded = VkMeshRenderer.RecordPreparedMeshDrawState(
                    secondary,
                    preparedDraw.RecordingState);
                if (!recorded)
                {
                    chain.State = CommandChainState.NotReady;
                    chain.DirtyReason |= CommandChainDirtyReason.PipelineGeneration;
                    throw new InvalidOperationException(
                        $"A prewarmed Vulkan command-chain draw became unavailable during secondary recording. " +
                        $"sourceIndex={opIndex} mesh='{preparedDraw.DiagnosticMeshName}' " +
                        $"uniformSlot={uniformSlot} preparedStateGeneration={preparedDraw.RecordingState.FrameDataGeneration}.");
                }
            }

            if (EndCommandBufferTracked(secondary) != Result.Success)
                throw new InvalidOperationException("Failed to end Vulkan worker mesh command-chain secondary command buffer.");

            chain.RecordedUniformSlotSignature = ComputeCommandChainUniformSlotSignature(
                batch.UniformSlots,
                chain.SourceStartIndex - batch.StartIndex,
                chain.SourceCount);

            chain.State = CommandChainState.Recorded;
            chain.FrameDataRefreshTouchedDescriptors = false;
            StoreCommandChainSecondaryInheritance(
                chain,
                inheritance.DynamicRendering,
                inheritance.RenderPass,
                inheritance.Framebuffer,
                inheritance.DynamicRenderingFormats,
                inheritance.DepthStencilReadOnly,
                inheritance.Samples,
                inheritance.LocalReadSignature,
                inheritance.RenderingFlags);
            MarkCommandChainSecondaryCommandBufferRecorded(chain);
        }

        internal bool TryRecordSecondaryBucket(
            CommandBuffer primaryCommandBuffer,
            uint imageIndex,
            HashSet<nint> executedCommandChainSecondaryHandles,
            FrameOp[] ops,
            int startIndex,
            VulkanSecondaryRecordingBucket bucket,
            int resolvedPassIndex,
            bool renderScopeActive,
            bool primaryQueryActive,
            string label)
        {
            VulkanSecondaryRecordingContract contract =
                EvaluateSecondaryRecordingContract(
                    ops,
                    startIndex,
                    bucket,
                    resolvedPassIndex,
                    renderScopeActive,
                    primaryQueryActive);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSecondaryRecordingEligibility(
                contract.Family,
                contract.Eligibility,
                Math.Max(1, bucket.Count));
            if (!contract.IsEligible)
                return false;

            if (CommandChainsEnabledForCurrentRecording)
            {
                ExecutePrimaryOwnedSecondaryCommandBufferBatch(
                    primaryCommandBuffer,
                    label,
                    imageIndex,
                    ops,
                    startIndex,
                    bucket.Count,
                    executedCommandChainSecondaryHandles,
                    contract.QueryInheritance,
                    (relativeIndex, secondary) =>
                    {
                        int opIndex = startIndex + relativeIndex;
                        FrameOp runOp = ops[opIndex];
                        RecordFrameOpInSecondary(secondary, imageIndex, runOp, opIndex);
                    });
                return true;
            }

            for (int relativeIndex = 0; relativeIndex < bucket.Count; relativeIndex++)
            {
                int opIndex = startIndex + relativeIndex;
                FrameOp runOp = ops[opIndex];
                ExecuteSecondaryCommandBuffer(
                    primaryCommandBuffer,
                    label,
                    imageIndex,
                    contract.QueryInheritance,
                    secondary => RecordFrameOpInSecondary(secondary, imageIndex, runOp, opIndex));
            }

            return true;
        }

        private void ExecutePrimaryOwnedSecondaryCommandBufferBatch(
            CommandBuffer primaryCommandBuffer,
            string label,
            uint imageIndex,
            FrameOp[] ops,
            int startIndex,
            int count,
            HashSet<nint> executedCommandChainSecondaryHandles,
            VulkanQuerySecondaryInheritanceContract queryInheritance,
            Action<int, CommandBuffer> recorder)
        {
            if (count <= 0)
                return;

            bool primaryLabelActive = false;
            if (CanRecordCommandBufferDebugLabels)
            {
                primaryLabelActive = CmdBeginLabel(primaryCommandBuffer, $"{label}PrimaryOwned");
            }

            CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(count);
            CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(count);
            Exception? firstError = null;
            object errorLock = new();

            try
            {
                Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(imageIndex);
                int commandBufferImageSlot = unchecked((int)Math.Min(imageIndex, int.MaxValue));
                for (int i = 0; i < count; i++)
                {
                    FrameOp op = ops[startIndex + i];
                    int primaryOwnedChainOrdinal = HashCode.Combine(startIndex, i, primaryCommandBuffer.Handle, 0x53454342);
                    CommandChainKey chainKey = new(
                        commandBufferImageSlot,
                        BuildRenderViewKey(op, dynamicOverlay: false),
                        op.PassIndex,
                        ResolveCommandChainTargetIdentity(op),
                        false,
                        primaryOwnedChainOrdinal);
                    CommandChain chain = GetOrCreateCommandChain(commandChainCache, chainKey);
                    if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, imageIndex, executedCommandChainSecondaryHandles, out CommandBuffer secondary))
                        throw new InvalidOperationException("Failed to allocate Vulkan primary-owned secondary command buffer.");

                    secondaryChains[i] = chain;
                    secondaryBuffers[i] = secondary;
                }

                void RecordSecondaryAt(int relativeIndex)
                {
                    CommandChain chain = secondaryChains[relativeIndex];
                    CommandBuffer secondary = secondaryBuffers[relativeIndex];

                    try
                    {
                        MarkCommandChainSecondaryCommandBufferInvalid(chain);
                        ResetVulkanCommandBufferTracked(secondary);

                        CommandBufferBeginInfo beginInfo = new()
                        {
                            SType = StructureType.CommandBufferBeginInfo,
                            Flags = CommandBufferUsageFlags.SimultaneousUseBit
                        };

                        CommandBufferInheritanceInfo inheritanceInfo = new()
                        {
                            SType = StructureType.CommandBufferInheritanceInfo,
                            RenderPass = default,
                            Subpass = 0,
                            Framebuffer = default,
                            OcclusionQueryEnable =
                                queryInheritance.OcclusionQueryEnable
                                    ? Vk.True
                                    : Vk.False,
                            QueryFlags = queryInheritance.QueryFlags,
                            PipelineStatistics =
                                queryInheritance.PipelineStatistics
                        };

                        beginInfo.PInheritanceInfo = &inheritanceInfo;

                        if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                            throw new Exception("Failed to begin Vulkan primary-owned secondary command buffer.");

                        ResetCommandBufferBindState(secondary);
                        MarkCommandChainSecondaryRecording(chain, secondary);
                        recorder(relativeIndex, secondary);

                        if (EndCommandBufferTracked(secondary) != Result.Success)
                            throw new Exception("Failed to end Vulkan primary-owned secondary command buffer.");

                        MarkCommandChainSecondaryCommandBufferRecorded(chain);
                    }
                    catch (Exception ex)
                    {
                        lock (errorLock)
                            firstError ??= ex;

                        DestroyCommandChainSecondaryCommandBuffer(chain);
                        secondaryBuffers[relativeIndex] = default;
                    }
                }

                for (int i = 0; i < count; i++)
                    RecordSecondaryAt(i);

                if (firstError is not null)
                    throw firstError;

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    CmdExecuteCommandsTracked(primaryCommandBuffer, (uint)count, secondaryPtr);
                for (int i = 0; i < count; i++)
                {
                    if (secondaryBuffers[i].Handle != 0)
                        executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                }
            }
            finally
            {
                Array.Clear(secondaryBuffers, 0, count);
                Array.Clear(secondaryChains, 0, count);
                ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                ArrayPool<CommandChain>.Shared.Return(secondaryChains);
                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }

        internal static bool TryGetSecondaryBucketForStart(
            IReadOnlyList<VulkanSecondaryRecordingBucket> buckets,
            Dictionary<int, VulkanSecondaryRecordingBucket>? bucketByStart,
            int startIndex,
            out VulkanSecondaryRecordingBucket bucket)
        {
            if (bucketByStart is not null)
                return bucketByStart.TryGetValue(startIndex, out bucket);

            for (int i = 0; i < buckets.Count; i++)
            {
                VulkanSecondaryRecordingBucket candidate = buckets[i];
                if (candidate.StartIndex == startIndex)
                {
                    bucket = candidate;
                    return true;
                }
            }

            bucket = default;
            return false;
        }

        private void RecordFrameOpInSecondary(CommandBuffer secondaryCommandBuffer, uint imageIndex, FrameOp runOp, int opIndex)
        {
            using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(runOp.Context.PipelineInstance);
            using var plannerScope = EnterFrameOpResourcePlannerReadbackScope(runOp.Context);
            switch (runOp)
            {
                case ComputeDispatchOp computeDispatchOp:
                    RecordComputeDispatchOp(secondaryCommandBuffer, imageIndex, computeDispatchOp, opIndex);
                    break;
                case BufferCopyOp bufferCopyOp:
                    RecordBufferCopyOp(secondaryCommandBuffer, bufferCopyOp);
                    break;
                case QueryOp
                {
                    Operation: ERenderQueryOperation.CopyResults,
                } queryOp:
                    if (queryOp.Query.CopyResults(
                            secondaryCommandBuffer,
                            queryOp.ResultDestination,
                            queryOp.ResultDestinationOffset,
                            queryOp.ResultStride,
                            queryOp.IncludeAvailability) !=
                        ERenderQueryReadStatus.Ready)
                    {
                        throw new InvalidOperationException(
                            "A prevalidated Vulkan query result copy became invalid during secondary recording.");
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Frame operation '{runOp.GetType().Name}' is not supported by the non-graphics secondary recorder.");
            }
        }

        private void ExecuteSecondaryCommandBuffer(
            CommandBuffer primaryCommandBuffer,
            string label,
            uint imageIndex,
            in VulkanQuerySecondaryInheritanceContract queryInheritance,
            Action<CommandBuffer> recorder)
        {
            bool primaryLabelActive = CmdBeginLabel(primaryCommandBuffer, label);
            CommandBuffer secondary = default;
            bool allocated = false;
            CommandPool pool = default;
            bool executedInPrimary = false;

            try
            {
                pool = GetThreadCommandPool();

                CommandBufferAllocateInfo allocInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Secondary,
                    CommandBufferCount = 1
                };

                Result allocateResult = AllocateVulkanCommandBuffersTracked(
                    ref allocInfo,
                    out secondary,
                    "SecondaryCommandBuffer.Worker");
                allocated = allocateResult == Result.Success && secondary.Handle != 0;
                if (!allocated)
                    throw new InvalidOperationException($"Failed to allocate Vulkan secondary command buffer ({allocateResult}).");
                if (allocated)
                {
                    RegisterCommandBufferImageIndex(secondary, imageIndex);
                    if (SupportsDebugUtils)
                        SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle), $"{label}.Secondary[{imageIndex}]");
                }

                CommandBufferBeginInfo beginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };

                CommandBufferInheritanceInfo inheritanceInfo = new()
                {
                    SType = StructureType.CommandBufferInheritanceInfo,
                    RenderPass = default,
                    Subpass = 0,
                    Framebuffer = default,
                    OcclusionQueryEnable =
                        queryInheritance.OcclusionQueryEnable
                            ? Vk.True
                            : Vk.False,
                    QueryFlags = queryInheritance.QueryFlags,
                    PipelineStatistics =
                        queryInheritance.PipelineStatistics
                };

                beginInfo.PInheritanceInfo = &inheritanceInfo;

                CommandBufferInheritanceDescriptorHeapInfoEXTNative descriptorHeapInheritanceInfo = default;
                BindHeapInfoEXTNative inheritedSamplerHeapInfo = default;
                BindHeapInfoEXTNative inheritedResourceHeapInfo = default;
                TryAppendDescriptorHeapInheritancePNext(
                    ref inheritanceInfo,
                    &descriptorHeapInheritanceInfo,
                    &inheritedSamplerHeapInfo,
                    &inheritedResourceHeapInfo);

                if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin Vulkan secondary command buffer.");

                ResetCommandBufferBindState(secondary);

                recorder(secondary);

                if (EndCommandBufferTracked(secondary) != Result.Success)
                    throw new Exception("Failed to end Vulkan secondary command buffer.");

                CmdExecuteCommandsTracked(primaryCommandBuffer, 1, &secondary);
                executedInPrimary = true;
            }
            finally
            {
                if (allocated && pool.Handle != 0)
                {
                    if (executedInPrimary)
                        DeferSecondaryCommandBufferFree(imageIndex, pool, secondary);
                    else
                    {
                        FreeVulkanCommandBufferTracked(pool, ref secondary, "SecondaryCommandBuffer.RecordFailure");
                        RemoveCommandBufferBindState(secondary);
                    }
                }

                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }

    }
}
