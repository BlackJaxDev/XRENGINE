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
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private bool TryEnsureMutableDynamicUiSecondaryCommandBuffer(
            uint imageIndex,
            PrimaryCommandArtifactOwner variant,
            out CommandBuffer secondaryCommandBuffer)
        {
            secondaryCommandBuffer = variant.DynamicUiSecondaryCommandBuffer;
            if (secondaryCommandBuffer.Handle != 0 &&
                CanResetSecondaryCommandBuffer(secondaryCommandBuffer))
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
            Result allocateResult = AllocateVulkanCommandBufferTracked(
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
            SetSecondaryDebugObjectName(
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
            PrimaryCommandArtifactOwner variant,
            FrameOperationSequence dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            bool forceRecord = false,
            bool includeDepthAttachment = true,
            SwapchainRecordingTarget recordingTarget = default,
            VulkanCommandRecordingPolicySnapshot policy = default)
        {
            forceRecord |= policy.FreshSerialRecording;
            if (dynamicUiBatchTextOps.Length == 0)
            {
                variant.DynamicUiOpCount = 0;
                variant.DynamicUiSignature = 0;
                variant.DynamicUiSecondaryRecorded = false;
                return true;
            }

            for (int operationIndex = 0;
                 operationIndex < dynamicUiBatchTextOps.Length;
                 operationIndex++)
            {
                if (dynamicUiBatchTextOps[operationIndex].IsSealedForFramePlan)
                    continue;

                variant.DynamicUiSecondaryRecorded = false;
                Debug.VulkanWarningEvery(
                    $"Vulkan.DynamicUi.UnsealedSecondaryInput.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Dynamic-UI secondary recording rejected an unsealed frame-plan operation at index {0}.",
                    operationIndex);
                return false;
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

            bool useDynamicRendering = policy.UseDynamicRendering &&
                recordingTarget.IsValid;

            RenderPass inheritedRenderPass = useDynamicRendering
                ? default
                : recordingTarget.LoadRenderPass;
            Framebuffer inheritedFramebuffer = useDynamicRendering
                ? default
                : recordingTarget.Framebuffer;

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
            colorAttachmentFormats[0] = recordingTarget.ImageFormat;

            DynamicRenderingFormatSignature dynamicRenderingFormats = useDynamicRendering
                ? includeDepthAttachment
                    ? CreateSwapchainDynamicRenderingFormatSignature(recordingTarget.ImageFormat, recordingTarget.DepthFormat)
                    : CreateSwapchainColorOnlyDynamicRenderingFormatSignature(recordingTarget.ImageFormat)
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
            frameDataManifest.Begin(MappedFrameArena?.Generation ?? 0UL, recordingScratch.DynamicUiMeshDrawSlotCapacityHint);
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
                    "MeshDraw",
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

            if (!frameDataManifest.TrySeal(MappedFrameArena?.Generation ?? 0UL, MappedFrameArena?.ReservedBytes ?? 0UL))
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
                ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.DynamicUiSecondary");
                if (Api!.BeginCommandBuffer(secondaryCommandBuffer, ref beginInfo) != Result.Success)
                    throw new Exception("Failed to begin dynamic UI text secondary command buffer.");

                ResetCommandBufferBindState(secondaryCommandBuffer);
                recordingStarted = true;
                meshDrawSlotsByRendererFamily.Clear();

                for (int i = 0; i < dynamicUiBatchTextOps.Length; i++)
                {
                    if (dynamicUiBatchTextOps[i] is not MeshDrawOp drawOp)
                        continue;

                    int opPassIndex = EnsureValidPassIndex(drawOp.PassIndex, "MeshDraw", drawOp.Context.PassMetadata);
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
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
                secondaryCommandBuffers: 1);

            recordingScratch.DynamicUiMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
            return true;
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

        internal bool TryRecordSecondaryBucket(
            CommandBuffer primaryCommandBuffer,
            uint imageIndex,
            HashSet<nint> executedCommandChainSecondaryHandles,
            FrameOperationSequence ops,
            CommandChainKey[]? scheduledKeysByOpIndex,
            Dictionary<CommandChainKey, CommandChain>? scheduledCache,
            int startIndex,
            VulkanSecondaryRecordingBucket bucket,
            int resolvedPassIndex,
            bool barrierPlanHasPass,
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
                    barrierPlanHasPass,
                    renderScopeActive,
                    primaryQueryActive);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSecondaryRecordingEligibility(
                contract.Family,
                contract.Eligibility,
                Math.Max(1, bucket.Count));
            if (!contract.IsEligible)
                return false;

            if (TryExecutePrimaryOwnedSecondaryCommandBufferBatch(
                    primaryCommandBuffer,
                    label,
                    imageIndex,
                    ops,
                    scheduledKeysByOpIndex,
                    scheduledCache,
                    startIndex,
                    bucket.Count,
                    executedCommandChainSecondaryHandles,
                    contract.QueryInheritance))
            {
                return true;
            }

            // Recording a transient secondary here would require a callback and
            // an ambient current-thread pool.  If the sealed schedule cannot
            // provide an owned artifact, leave the operation to the primary's
            // established inline fallback.
            return false;
        }

        private bool TryExecutePrimaryOwnedSecondaryCommandBufferBatch(
            CommandBuffer primaryCommandBuffer,
            string label,
            uint imageIndex,
            FrameOperationSequence ops,
            CommandChainKey[]? scheduledKeysByOpIndex,
            Dictionary<CommandChainKey, CommandChain>? scheduledCache,
            int startIndex,
            int count,
            HashSet<nint> executedCommandChainSecondaryHandles,
            VulkanQuerySecondaryInheritanceContract queryInheritance)
        {
            if (count <= 0 || scheduledKeysByOpIndex is null || scheduledCache is null)
                return false;
            for (int i = 0; i < count; i++)
            {
                int opIndex = startIndex + i;
                if ((uint)opIndex >= (uint)scheduledKeysByOpIndex.Length ||
                    scheduledKeysByOpIndex[opIndex].ChainOrdinal == -1 ||
                    !scheduledCache.TryGetValue(scheduledKeysByOpIndex[opIndex], out CommandChain? scheduled) ||
                    scheduled.SourceStartIndex != opIndex || scheduled.SourceCount != 1 ||
                    scheduled.PacketSnapshot is not { IsSealed: true })
                    return false;
            }

            bool primaryLabelActive = false;
            if (CanRecordCommandBufferDebugLabels)
            {
                primaryLabelActive = CmdBeginLabel(primaryCommandBuffer, $"{label}PrimaryOwned");
            }

            CommandBufferRecordingScratch batchScratch = _commandBufferRecordingScratch.Value!;
            batchScratch.EnsureNonGraphicsSecondaryCapacity(count);
            CommandBuffer[] secondaryBuffers = batchScratch.NonGraphicsSecondaryBuffers;
            CommandChain[] secondaryChains = batchScratch.NonGraphicsSecondaryChains;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    int opIndex = startIndex + i;
                    CommandChain chain = scheduledCache[scheduledKeysByOpIndex[opIndex]];
                    if (!TryEnsureMutableCommandChainSecondaryCommandBuffer(chain, imageIndex, executedCommandChainSecondaryHandles, out CommandBuffer secondary))
                        throw new InvalidOperationException("Failed to allocate Vulkan primary-owned secondary command buffer.");

                    secondaryChains[i] = chain;
                    secondaryBuffers[i] = secondary;
                }

                void RecordSecondaryAt(int relativeIndex)
                {
                    CommandChain chain = secondaryChains[relativeIndex];
                    CommandBuffer secondary = secondaryBuffers[relativeIndex];
                    RenderPacket packet = chain.PacketSnapshot!;
                    FrameOp op = ops[packet.SourceStartIndex];

                    try
                    {
                        // Fixed explicit memory barriers have no mutable native
                        // payload. Their schedule identity and mask are the full
                        // prepared key, so retain the executable artifact.
                        ulong preparedSignature = ComputeNonGraphicsSecondaryPreparedSignature(op);
                        if (op is MemoryBarrierOp &&
                            chain.StructuralSignature == preparedSignature &&
                            chain.RecordedArtifact.IsExecutable)
                            return;

                        if (op is ComputeDispatchOp or ComputeDispatchIndirectOp &&
                            TryCapturePreparedNonGraphicsComputeKey(op, chain, out VulkanPreparedCommandChainKey currentKey) &&
                            chain.PreparedKey.IsComplete &&
                            chain.PreparedKey.Matches(in currentKey) &&
                            chain.RecordedArtifact.IsExecutable)
                            return;

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

                        ThrowIfVulkanDeviceOperationNotAdmitted("vkBeginCommandBuffer.CommandChainSecondary");
                        if (Api!.BeginCommandBuffer(secondary, ref beginInfo) != Result.Success)
                            throw new Exception("Failed to begin Vulkan primary-owned secondary command buffer.");

                        ResetCommandBufferBindState(secondary);
                        MarkCommandChainSecondaryRecording(chain, secondary);
                        CommandBufferRecordingScratch recordingScratch =
                            _commandBufferRecordingScratch.Value!;
                        recordingScratch.PreparedComputePayload = null;
                        int opIndex = packet.SourceStartIndex;
                        RecordFrameOpInSecondary(
                            secondary,
                            imageIndex,
                            op,
                            opIndex,
                            ResolveCommandChainInlineOperationIndex(ops, opIndex),
                            packet);
                        if (op is ComputeDispatchOp or ComputeDispatchIndirectOp)
                            chain.PreparedComputePayload =
                                recordingScratch.PreparedComputePayload;

                        if (EndCommandBufferTracked(secondary) != Result.Success)
                            throw new Exception("Failed to end Vulkan primary-owned secondary command buffer.");

                        chain.StructuralSignature = preparedSignature;
                        if (op is ComputeDispatchOp or ComputeDispatchIndirectOp &&
                            TryCapturePreparedNonGraphicsComputeKey(op, chain, out VulkanPreparedCommandChainKey preparedKey))
                        {
                            VulkanPreparedCommandChainAuthority authority =
                                chain.PreparedAuthority is { } currentAuthority &&
                                currentAuthority.PreparedKey.Matches(in preparedKey)
                                    ? currentAuthority
                                    : new VulkanPreparedCommandChainAuthority(preparedKey);
                            PublishPreparedCommandChainAuthority(chain, authority);
                        }
                        else if (op is ComputeDispatchOp or ComputeDispatchIndirectOp)
                            chain.PreparedKey = VulkanPreparedCommandChainKey.Incomplete;
                        MarkCommandChainSecondaryCommandBufferRecorded(chain);
                    }
                    catch (Exception ex)
                    {
                        DestroyCommandChainSecondaryCommandBuffer(chain);
                        secondaryBuffers[relativeIndex] = default;
                        throw new InvalidOperationException(
                            $"Failed to record scheduled non-graphics secondary at relative index {relativeIndex}.",
                            ex);
                    }
                }

                for (int i = 0; i < count; i++)
                    RecordSecondaryAt(i);

                fixed (CommandBuffer* secondaryPtr = secondaryBuffers)
                    CmdExecuteCommandsTracked(primaryCommandBuffer, (uint)count, secondaryPtr);
                for (int i = 0; i < count; i++)
                {
                    if (secondaryBuffers[i].Handle != 0)
                        executedCommandChainSecondaryHandles.Add(secondaryBuffers[i].Handle);
                }
                return true;
            }
            finally
            {
                Array.Clear(secondaryBuffers, 0, count);
                Array.Clear(secondaryChains, 0, count);
                if (primaryLabelActive)
                    CmdEndLabel(primaryCommandBuffer);
            }
        }

        private static ulong ComputeNonGraphicsSecondaryPreparedSignature(FrameOp operation)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(operation.PassIndex);
            hash.Add((int)operation.Kind);
            switch (operation)
            {
                case MemoryBarrierOp barrier: hash.Add((int)barrier.Mask); break;
                case ComputeDispatchOp dispatch:
                    hash.Add(dispatch.Program?.BindingId ?? 0u); hash.Add(dispatch.Program?.LinkGeneration ?? 0UL);
                    hash.Add(dispatch.GroupsX); hash.Add(dispatch.GroupsY); hash.Add(dispatch.GroupsZ); break;
                case ComputeDispatchIndirectOp indirect:
                    hash.Add(indirect.Program?.BindingId ?? 0u); hash.Add(indirect.Program?.LinkGeneration ?? 0UL);
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ArgumentOwner));
                    hash.Add(indirect.ArgumentBuffer.Handle);
                    hash.Add(indirect.ArgumentOffset);
                    break;
            }
            return hash.ToHash();
        }

        private bool TryCapturePreparedNonGraphicsComputeKey(
            FrameOp operation,
            CommandChain chain,
            out VulkanPreparedCommandChainKey key)
        {
            key = VulkanPreparedCommandChainKey.Incomplete;
            VulkanPreparedComputePayload? payload = chain.PreparedComputePayload;
            VkRenderProgram? program = operation switch
            {
                ComputeDispatchOp direct => direct.Program,
                ComputeDispatchIndirectOp indirect => indirect.Program,
                _ => null,
            };
            if (!payload.HasValue || program is null ||
                program.ComputePipeline.Handle == 0 || program.PipelineLayout.Handle == 0)
                return false;

            VulkanRecordedDescriptorSetIdentityBuffer sets =
                CaptureRecordedDescriptorSetIdentities(payload.Value.DescriptorSets, default);
            if (!sets.IsComplete)
                return false;
            ulong pipelineGeneration = GetCurrentVulkanResourceGeneration(ObjectType.Pipeline, program.ComputePipeline.Handle);
            ulong layoutGeneration = GetCurrentVulkanResourceGeneration(ObjectType.PipelineLayout, program.PipelineLayout.Handle);
            if (pipelineGeneration == 0 || layoutGeneration == 0)
                return false;
            FrameOpSignatureHasher pipeline = new();
            pipeline.Add(program.BindingId); pipeline.Add(program.LinkGeneration);
            pipeline.Add(program.ComputePipeline.Handle); pipeline.Add(pipelineGeneration);
            pipeline.Add(program.PipelineLayout.Handle); pipeline.Add(layoutGeneration);
            DescriptorBindingSnapshot descriptorSnapshot =
                CreateDescriptorSnapshot(operation);
            ResourcePlanSnapshot resourceSnapshot = new(
                Revision: chain.ResourcePlanRevision,
                PhysicalImageSignature: 0UL,
                FramebufferSignature: 0UL,
                PipelineGeneration: ResolvePipelineGeneration(operation),
                RenderArea: 0UL,
                QueueFamily: operation.Context.SubmissionQueueFamily,
                NativeTarget: default);
            RecordedPacketKey packetKey = CaptureRecordedPacketKey(
                operation,
                nativeTarget: default,
                descriptorSnapshot,
                resourceSnapshot) with
            {
                DescriptorSets = sets,
            };
            RenderPacket? packet = chain.PacketSnapshot;
            if (packet is null)
                return false;
            RecordedPacketKey expected = packet.RecordedPacketKey with
            {
                DescriptorSets = sets,
            };
            if (!expected.IsComplete || !expected.Matches(in packetKey))
            {
                // A non-graphics secondary may execute once for the current
                // frame, but it is not reusable unless its post-binding key is
                // the exact completion of the immutable packet snapshot.
                return false;
            }
            key = new VulkanPreparedCommandChainKey(
                pipeline.ToHash(), ComputeRecordedDescriptorSetIdentityHash(sets), sets.Count,
                packetKey, IsComplete: packetKey.IsComplete);
            return key.IsComplete;
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

        private void RecordFrameOpInSecondary(
            CommandBuffer secondaryCommandBuffer,
            uint imageIndex,
            FrameOp runOp,
            int opIndex,
            int descriptorBindingOrdinal,
            RenderPacket? packet)
        {
            if (packet is not null &&
                (!packet.IsSealed || packet.SourceStartIndex != opIndex || packet.SourceCount != 1))
                throw new InvalidOperationException("Non-graphics secondary recording received a stale render-packet snapshot.");
            using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(runOp.Context.PipelineInstance);
            switch (runOp)
            {
                case ComputeDispatchOp computeDispatchOp:
                    // Reusable compute descriptors are prepared and refreshed by
                    // their thin-primary ordinal. Source indices include dynamic
                    // secondary-owned operations and therefore are not a stable
                    // cache identity for this binding.
                    RecordComputeDispatchOp(
                        secondaryCommandBuffer,
                        imageIndex,
                        computeDispatchOp,
                        descriptorBindingOrdinal);
                    break;
                case ComputeDispatchIndirectOp computeDispatchIndirectOp:
                    _commandRuntime.RecordComputeDispatchIndirectOp(secondaryCommandBuffer, imageIndex, computeDispatchIndirectOp);
                    break;
                case MemoryBarrierOp memoryBarrierOp:
                    EmitMemoryBarrierMask(secondaryCommandBuffer, memoryBarrierOp.Mask);
                    break;
                case BufferCopyOp bufferCopyOp:
                    _commandRuntime.RecordBufferCopyOp(secondaryCommandBuffer, bufferCopyOp);
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
                        $"Frame operation '{GetFrameOpDiagnosticName(runOp)}' is not supported by the non-graphics secondary recorder.");
            }
        }

    }
}
