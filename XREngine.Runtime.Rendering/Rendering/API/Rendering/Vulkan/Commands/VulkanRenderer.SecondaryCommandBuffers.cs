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
        private bool RecordDynamicUiBatchTextSecondaryCommandBuffer(
            uint imageIndex,
            CommandBufferCacheVariant variant,
            FrameOp[] dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            bool forceRecord = false)
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
                variant.DynamicUiSecondaryRecorded)
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

            CommandBuffer secondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
            if (secondaryCommandBuffer.Handle == 0)
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "dynamic-ui-text",
                    null,
                    dynamicUiBatchTextOps[0].PassIndex,
                    "secondary command buffer handle is zero");
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

            Api!.ResetCommandBuffer(secondaryCommandBuffer, 0);

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
                ? CreateSwapchainColorOnlyDynamicRenderingFormatSignature(swapChainImageFormat)
                : default;

            CommandBufferInheritanceRenderingInfo renderingInheritanceInfo = new()
            {
                SType = StructureType.CommandBufferInheritanceRenderingInfo,
                Flags = 0,
                ViewMask = dynamicRenderingFormats.ViewMask,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = colorAttachmentFormats,
                DepthAttachmentFormat = Format.Undefined,
                StencilAttachmentFormat = Format.Undefined,
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
                Flags = CommandBufferUsageFlags.RenderPassContinueBit,
                PInheritanceInfo = &inheritanceInfo,
            };

            if (Api!.BeginCommandBuffer(secondaryCommandBuffer, ref beginInfo) != Result.Success)
                throw new Exception("Failed to begin dynamic UI text secondary command buffer.");

            ResetCommandBufferBindState(secondaryCommandBuffer);

            Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = _dynamicUiMeshDrawSlotsByRendererScratch;
            meshDrawSlotsByRenderer.Clear();
            meshDrawSlotsByRenderer.EnsureCapacity(_dynamicUiMeshDrawSlotCapacityHint);
            EnsureMeshDrawUniformSlotCapacityForRecording(dynamicUiBatchTextOps, meshDrawSlotsByRenderer);
            meshDrawSlotsByRenderer.Clear();
            int GetMeshDrawUniformSlot(VkMeshRenderer renderer)
            {
                ref int slotRef = ref CollectionsMarshal.GetValueRefOrAddDefault(meshDrawSlotsByRenderer, renderer, out _);
                int slot = slotRef;
                slotRef = slot + 1;
                return slot;
            }

            int recordedDrawCount = 0;
            for (int i = 0; i < dynamicUiBatchTextOps.Length; i++)
            {
                if (dynamicUiBatchTextOps[i] is not MeshDrawOp drawOp)
                    continue;

                int opPassIndex = EnsureValidPassIndex(drawOp.PassIndex, drawOp.GetType().Name, drawOp.Context.PassMetadata);
                if (opPassIndex == int.MinValue)
                    continue;

                using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawOp.Context.PipelineInstance);

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

                int drawUniformSlot = GetMeshDrawUniformSlot(drawOp.Draw.Renderer);
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

            if (Api!.EndCommandBuffer(secondaryCommandBuffer) != Result.Success)
                throw new Exception("Failed to end dynamic UI text secondary command buffer.");

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
            if (CommandChainsEnabledForCurrentRecording)
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(secondaryCommandBuffers: 1);

            _dynamicUiMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
            return true;
        }

        private bool TryRecordDynamicUiBatchTextOverlayCommandBuffer(
            uint imageIndex,
            CommandBuffer secondaryCommandBuffer,
            int dynamicUiBatchTextOpCount,
            ImageLayout initialSwapchainLayout,
            CommandBufferCacheVariant? dynamicUiBatchTextVariant,
            FrameOp[] dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            out CommandBuffer overlayCommandBuffer)
        {
            overlayCommandBuffer = default;
            if (dynamicUiBatchTextVariant is not null)
            {
                if (dynamicUiBatchTextOps.Length == 0 ||
                    !RecordDynamicUiBatchTextSecondaryCommandBuffer(
                        imageIndex,
                        dynamicUiBatchTextVariant,
                        dynamicUiBatchTextOps,
                        dynamicUiBatchTextSignature,
                        forceRecord: true))
                {
                    return false;
                }

                secondaryCommandBuffer = dynamicUiBatchTextVariant.DynamicUiSecondaryCommandBuffer;
                dynamicUiBatchTextOpCount = dynamicUiBatchTextVariant.DynamicUiOpCount;
            }

            if (dynamicUiBatchTextOpCount <= 0 ||
                secondaryCommandBuffer.Handle == 0 ||
                _dynamicUiBatchTextOverlayCommandBuffers is null ||
                imageIndex >= _dynamicUiBatchTextOverlayCommandBuffers.Length)
            {
                return false;
            }

            bool useDynamicRendering = UseDynamicRenderingRenderTargets &&
                swapChainImageViews is not null &&
                imageIndex < swapChainImageViews.Length;
            if (!useDynamicRendering)
                return false;

            CommandBuffer commandBuffer = _dynamicUiBatchTextOverlayCommandBuffers[imageIndex];
            Api!.ResetCommandBuffer(commandBuffer, 0);

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            if (Api.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
                throw new InvalidOperationException("Failed to begin dynamic UI text overlay command buffer.");

            ResetCommandBufferBindState(commandBuffer);
            CmdBeginLabel(commandBuffer, "DynamicUIBatchTextOverlay");

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
            Api.CmdExecuteCommands(commandBuffer, 1, &secondaryCommandBuffer);
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

            if (Api.EndCommandBuffer(commandBuffer) != Result.Success)
                throw new InvalidOperationException("Failed to end dynamic UI text overlay command buffer.");

            overlayCommandBuffer = commandBuffer;
            return true;
        }

        private bool TryRecordSecondaryBucket(
            CommandBuffer primaryCommandBuffer,
            uint imageIndex,
            HashSet<nint> executedCommandChainSecondaryHandles,
            FrameOp[] ops,
            int startIndex,
            VulkanRenderGraphCompiler.SecondaryRecordingBucket bucket,
            string label)
        {
            if (!_enableSecondaryCommandBuffers || bucket.Count <= 0)
                return false;

            bool useParallelSecondary =
                _enableParallelSecondaryCommandBufferRecording &&
                bucket.Count >= Math.Max(_parallelSecondaryIndirectRunThreshold, 2);

            if (CommandChainsEnabledForCurrentRecording)
            {
                ExecutePrimaryOwnedSecondaryCommandBufferBatch(
                    primaryCommandBuffer,
                    label,
                    imageIndex,
                    ops,
                    startIndex,
                    bucket.Count,
                    useParallelSecondary,
                    executedCommandChainSecondaryHandles,
                    (relativeIndex, secondary) =>
                    {
                        int opIndex = startIndex + relativeIndex;
                        FrameOp runOp = ops[opIndex];
                        RecordFrameOpInSecondary(secondary, imageIndex, runOp, opIndex);
                    });
                return true;
            }

            if (bucket.Count > 1 && useParallelSecondary)
            {
                ExecuteSecondaryCommandBufferBatchParallel(
                    primaryCommandBuffer,
                    $"{label}Batch",
                    bucket.Count,
                    imageIndex,
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
            bool useParallelSecondary,
            HashSet<nint> executedCommandChainSecondaryHandles,
            Action<int, CommandBuffer> recorder)
        {
            if (count <= 0)
                return;

            bool primaryLabelActive = false;
            if (CanRecordCommandBufferDebugLabels)
            {
                primaryLabelActive = CmdBeginLabel(primaryCommandBuffer, useParallelSecondary && count > 1
                    ? $"{label}PrimaryOwnedBatch"
                    : $"{label}PrimaryOwned");
            }

            CommandBuffer[] secondaryBuffers = ArrayPool<CommandBuffer>.Shared.Rent(count);
            CommandChain[] secondaryChains = ArrayPool<CommandChain>.Shared.Rent(count);
            Task[]? tasks = useParallelSecondary && count > 1
                ? ArrayPool<Task>.Shared.Rent(count)
                : null;
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
                        Api!.ResetCommandBuffer(secondary, 0);

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
                            OcclusionQueryEnable = Vk.False,
                            QueryFlags = QueryControlFlags.None,
                            PipelineStatistics = QueryPipelineStatisticFlags.None
                        };

                        beginInfo.PInheritanceInfo = &inheritanceInfo;

                        if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                            throw new Exception("Failed to begin Vulkan primary-owned secondary command buffer.");

                        ResetCommandBufferBindState(secondary);
                        recorder(relativeIndex, secondary);

                        if (Api!.EndCommandBuffer(secondary) != Result.Success)
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

                if (tasks is not null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int taskIndex = i;
                        tasks[i] = Task.Run(() => RecordSecondaryAt(taskIndex));
                    }

                    for (int i = 0; i < count; i++)
                        tasks[i]!.Wait();
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        RecordSecondaryAt(i);
                }

                if (firstError is not null)
                    throw firstError;

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    Api!.CmdExecuteCommands(primaryCommandBuffer, (uint)count, secondaryPtr);
                for (int i = 0; i < count; i++)
                {
                    if (secondaryBuffers[i].Handle != 0)
                        executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                }
            }
            finally
            {
                if (tasks is not null)
                {
                    Array.Clear(tasks, 0, count);
                    ArrayPool<Task>.Shared.Return(tasks);
                }

                Array.Clear(secondaryBuffers, 0, count);
                Array.Clear(secondaryChains, 0, count);
                ArrayPool<CommandBuffer>.Shared.Return(secondaryBuffers);
                ArrayPool<CommandChain>.Shared.Return(secondaryChains);
                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }

        private static bool TryGetSecondaryBucketForStart(
            IReadOnlyList<VulkanRenderGraphCompiler.SecondaryRecordingBucket> buckets,
            Dictionary<int, VulkanRenderGraphCompiler.SecondaryRecordingBucket>? bucketByStart,
            int startIndex,
            out VulkanRenderGraphCompiler.SecondaryRecordingBucket bucket)
        {
            if (bucketByStart is not null)
                return bucketByStart.TryGetValue(startIndex, out bucket);

            for (int i = 0; i < buckets.Count; i++)
            {
                VulkanRenderGraphCompiler.SecondaryRecordingBucket candidate = buckets[i];
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
            using IDisposable? _ = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(runOp.Context.PipelineInstance);
            switch (runOp)
            {
                case BlitOp blitOp:
                    RecordBlitOp(secondaryCommandBuffer, imageIndex, blitOp);
                    break;
                case IndirectDrawOp indirectDrawOp:
                    RecordIndirectDrawOp(secondaryCommandBuffer, indirectDrawOp);
                    break;
                case MeshTaskDispatchIndirectCountOp meshTaskDispatchOp:
                    RecordMeshTaskDispatchIndirectCountOp(secondaryCommandBuffer, meshTaskDispatchOp);
                    break;
                case ComputeDispatchOp computeDispatchOp:
                    RecordComputeDispatchOp(secondaryCommandBuffer, imageIndex, computeDispatchOp, opIndex);
                    break;
                case MemoryBarrierOp memoryBarrierOp:
                    EmitMemoryBarrierMask(secondaryCommandBuffer, memoryBarrierOp.Mask);
                    break;
                case PublishFramebufferForSamplingOp publishOp:
                    RecordPublishFramebufferForSamplingOp(secondaryCommandBuffer, publishOp);
                    break;
            }
        }

        private void ExecuteSecondaryCommandBuffer(CommandBuffer primaryCommandBuffer, string label, uint imageIndex, Action<CommandBuffer> recorder)
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

                Api!.AllocateCommandBuffers(device, ref allocInfo, out secondary);
                allocated = secondary.Handle != 0;
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
                    OcclusionQueryEnable = Vk.False,
                    QueryFlags = QueryControlFlags.None,
                    PipelineStatistics = QueryPipelineStatisticFlags.None
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

                if (Api!.EndCommandBuffer(secondary) != Result.Success)
                    throw new Exception("Failed to end Vulkan secondary command buffer.");

                Api!.CmdExecuteCommands(primaryCommandBuffer, 1, &secondary);
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
                        Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                        RemoveCommandBufferBindState(secondary);
                    }
                }

                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }

        private void ExecuteSecondaryCommandBufferBatchParallel(
            CommandBuffer primaryCommandBuffer,
            string label,
            int count,
            uint imageIndex,
            Action<int, CommandBuffer> recorder)
        {
            if (count <= 0)
                return;

            if (count == 1)
            {
                ExecuteSecondaryCommandBuffer(primaryCommandBuffer, label, imageIndex, cmd => recorder(0, cmd));
                return;
            }

            bool primaryLabelActive = CmdBeginLabel(primaryCommandBuffer, label);
            CommandBuffer[] secondaryBuffers = new CommandBuffer[count];
            CommandPool[] ownerPools = new CommandPool[count];
            bool[] allocated = new bool[count];
            Exception? firstError = null;
            object errorLock = new();
            bool executedInPrimary = false;

            try
            {
                Task[] tasks = new Task[count];
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    tasks[index] = Task.Run(() =>
                    {
                        if (firstError is not null)
                            return;

                        CommandBuffer secondary = default;
                        bool localAllocated = false;
                        CommandPool pool = default;

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

                            Api!.AllocateCommandBuffers(device, ref allocInfo, out secondary);
                            localAllocated = secondary.Handle != 0;
                            if (localAllocated)
                            {
                                RegisterCommandBufferImageIndex(secondary, imageIndex);
                                if (SupportsDebugUtils)
                                    SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle), $"{label}.Secondary[{imageIndex}:{index}]");
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
                                OcclusionQueryEnable = Vk.False,
                                QueryFlags = QueryControlFlags.None,
                                PipelineStatistics = QueryPipelineStatisticFlags.None
                            };

                            beginInfo.PInheritanceInfo = &inheritanceInfo;

                            if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                                throw new Exception("Failed to begin Vulkan secondary command buffer.");

                            ResetCommandBufferBindState(secondary);

                            recorder(index, secondary);

                            if (Api!.EndCommandBuffer(secondary) != Result.Success)
                                throw new Exception("Failed to end Vulkan secondary command buffer.");

                            secondaryBuffers[index] = secondary;
                            ownerPools[index] = pool;
                            allocated[index] = localAllocated;
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock)
                            {
                                firstError ??= ex;
                            }

                            if (localAllocated && pool.Handle != 0)
                            {
                                try
                                {
                                    Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                                    RemoveCommandBufferBindState(secondary);
                                }
                                catch
                                {
                                }
                            }
                        }
                    });
                }

                Task.WaitAll(tasks);

                if (firstError is not null)
                    throw firstError;

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    Api!.CmdExecuteCommands(primaryCommandBuffer, (uint)count, secondaryPtr);

                executedInPrimary = true;
            }
            finally
            {
                for (int i = 0; i < count; i++)
                {
                    if (!allocated[i] || ownerPools[i].Handle == 0 || secondaryBuffers[i].Handle == 0)
                        continue;

                    if (executedInPrimary)
                        DeferSecondaryCommandBufferFree(imageIndex, ownerPools[i], secondaryBuffers[i]);
                    else
                    {
                        Api!.FreeCommandBuffers(device, ownerPools[i], 1, ref secondaryBuffers[i]);
                        RemoveCommandBufferBindState(secondaryBuffers[i]);
                    }
                }

                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }
    }
}
