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
    internal sealed partial class VulkanCommandRuntime
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

        private unsafe bool RecordDynamicUiBatchTextSecondaryCommandBuffer(
            uint imageIndex,
            PrimaryCommandArtifactOwner variant,
            FrameOperationSequence dynamicUiBatchTextOps,
            ulong dynamicUiBatchTextSignature,
            bool forceRecord = false,
            bool includeDepthAttachment = true,
            bool releaseInlinePrimaryBeforeSecondaryMutation = false,
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

            // A secondary cannot be reset while a completed immutable primary
            // still executes it. Delayed UI has its own per-image overlay
            // primary, so release only that artifact and retain the scene
            // primary cache. Inline UI is owned by the primary being rebuilt;
            // the caller explicitly admits releasing that primary before this
            // secondary is changed.
            CommandBuffer parentCommandBuffer = default;
            if (!includeDepthAttachment)
            {
                CommandBuffer[]? overlays = CommandBuffers.DynamicUiOverlays;
                if (overlays is null || imageIndex >= overlays.Length)
                    return false;
                parentCommandBuffer = overlays[imageIndex];
            }
            else if (releaseInlinePrimaryBeforeSecondaryMutation)
            {
                // The tracked reset below happens before the primary recorder
                // can publish a replacement. Keep this owner ineligible for
                // reuse if any later secondary or primary recording step fails.
                variant.Dirty = true;
                variant.DirtyReason = "DynamicUiSecondaryMutation";
                parentCommandBuffer = variant.PrimaryCommandBuffer;
            }

            if (parentCommandBuffer.Handle != 0)
            {
                if (!TryResetCommandBufferWithLifetime(
                        parentCommandBuffer,
                        "DynamicUiSecondaryParentRelease",
                        out Result parentResetResult))
                {
                    return false;
                }
                if (parentResetResult != Result.Success)
                {
                    throw new InvalidOperationException(
                        $"Failed to reset dynamic UI secondary parent command buffer: {parentResetResult}.");
                }
            }

            // An exact immutable-secondary hit consumes only the signature and
            // existing command-buffer artifact, so it is safe to admit before a
            // new FramePlan has been lowered. Any path that will encode commands
            // still requires plan-owned sealed payloads below.
            for (int operationIndex = 0;
                 operationIndex < dynamicUiBatchTextOps.Length;
                 operationIndex++)
            {
                if (dynamicUiBatchTextOps.GetHeader(operationIndex).OpCode ==
                    EVulkanPrimaryPlanNodeKind.MeshDraw)
                    continue;
            }

            if (!TryEnsureMutableDynamicUiSecondaryCommandBuffer(
                    imageIndex,
                    variant,
                    out CommandBuffer secondaryCommandBuffer))
            {
                LogCommandChainSecondaryInheritanceMismatch(
                    "dynamic-ui-text",
                    null,
                    dynamicUiBatchTextOps.GetHeader(0).PassIndex,
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
                    dynamicUiBatchTextOps.GetHeader(0).PassIndex,
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
                    default,
                    dynamicUiBatchTextOps,
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    sealAfterRegister: true,
                    meshDrawSlotsByRenderer,
                    recordingScratch,
                    recordingScratch.DynamicUiMeshFrameDataFamilyBases,
                    0UL,
                    0UL,
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
                if (dynamicUiBatchTextOps.GetHeader(i).OpCode !=
                    EVulkanPrimaryPlanNodeKind.MeshDraw)
                    continue;
                ref readonly MeshDrawPayload drawOp =
                    ref dynamicUiBatchTextOps.GetMeshDraw(i);
                ref readonly FrameOpContext drawContext =
                    ref dynamicUiBatchTextOps.GetContext(i);
                int drawSlot = GetFrameWideMeshDrawUniformSlot(
                    meshDrawSlotsByRendererFamily,
                    meshFrameDataFamilyBases,
                    drawOp.Draw.Renderer,
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    EVulkanMeshFrameDataStreamKind.DynamicUi,
                    drawContext,
                    drawOp.Draw);
                using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(
                    drawContext.PipelineInstance);
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

                int pipelinePassIndex = VulkanCommandRuntime.EnsureValidPassIndex(
                    dynamicUiBatchTextOps.GetHeader(i).PassIndex,
                    "MeshDraw",
                    drawContext.PassMetadata);
                if (pipelinePassIndex == int.MinValue ||
                    drawOp.Draw.Renderer.TryPrewarmGraphicsPipelinesForRecording(
                        drawOp.Draw,
                        inheritedRenderPass,
                        useDynamicRendering,
                        dynamicRenderingFormats,
                        pipelinePassIndex,
                        drawContext.PassMetadata,
                        depthStencilReadOnly: false,
                        drawContext.PipelineInstance?.DebugName ?? "<no pipeline>",
                        foregroundRequired: false,
                        out _,
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
                if (BeginTrackedCommandBuffer(
                        secondaryCommandBuffer,
                        ref beginInfo,
                        "DynamicUiSecondary") != Result.Success)
                    throw new Exception("Failed to begin dynamic UI text secondary command buffer.");

                ulong recGen = CommandBuffers.ResolveRecordingGeneration(secondaryCommandBuffer);
                LaneRecordingContexts.BeginContext(
                    EVulkanAcceptedFrameLane.Ui,
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    secondaryCommandBuffer,
                    recGen);

                recordingStarted = true;
                meshDrawSlotsByRendererFamily.Clear();

                for (int i = 0; i < dynamicUiBatchTextOps.Length; i++)
                {
                    if (dynamicUiBatchTextOps.GetHeader(i).OpCode !=
                        EVulkanPrimaryPlanNodeKind.MeshDraw)
                        continue;
                    ref readonly MeshDrawPayload drawOp =
                        ref dynamicUiBatchTextOps.GetMeshDraw(i);
                    ref readonly FrameOpContext drawContext =
                        ref dynamicUiBatchTextOps.GetContext(i);

                    int opPassIndex = VulkanCommandRuntime.EnsureValidPassIndex(dynamicUiBatchTextOps.GetHeader(i).PassIndex, "MeshDraw", drawContext.PassMetadata);
                    if (opPassIndex == int.MinValue)
                        continue;

                    using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(drawContext.PipelineInstance);

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
                        drawContext,
                        drawOp.Draw);
                    bool recordedDraw = drawOp.Draw.Renderer.RecordDraw(
                        secondaryCommandBuffer,
                        drawOp.Draw,
                        inheritedRenderPass,
                        useDynamicRendering,
                        dynamicRenderingFormats,
                        opPassIndex,
                        drawContext.PassMetadata,
                        dynamicUiBatchTextOps.GetTarget(i),
                        drawContext,
                        false,
                        drawContext.PipelineInstance?.DebugName ?? "<no pipeline>",
                        dynamicUiBatchTextOps.GetTarget(i)?.Name ?? "<swapchain>",
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

        internal static ulong ComputeCommandChainUniformSlotSignature(
            VulkanCommandChainRecordingDraw[] draws,
            int startIndex,
            int count)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(count);
            for (int i = 0; i < count; i++)
                hash.Add(draws[startIndex + i].UniformSlot);
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

            // Standalone compute/barrier/transfer/query chains are currently
            // one operation each. Dispatching those legacy artifacts would
            // violate the coarse-recording floor, so keep the whole bucket on
            // the primary until it has a typed immutable range encoder capable
            // of placing at least MinMeshDrawsPerRenderPacket equivalents in
            // one lane-owned transient secondary.
            return false;
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
            FrameOperationSequence operations,
            int opIndex,
            int descriptorBindingOrdinal,
            RenderPacket? packet)
        {
            if (packet is not null &&
                (!packet.IsSealed || packet.SourceStartIndex != opIndex || packet.SourceCount != 1))
                throw new InvalidOperationException("Non-graphics secondary recording received a stale render-packet snapshot.");
            ref readonly FrameOperationHeader header = ref operations.GetHeader(opIndex);
            using var pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(operations.GetContext(opIndex).PipelineInstance);
            switch (header.OpCode)
            {
                case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                    // Descriptor identity is the direct-compute occurrence in
                    // the sealed stream, including secondary-owned dispatches.
                    ref readonly ComputeDispatchPayload dispatch =
                        ref operations.GetComputeDispatch(opIndex);
                    ref readonly FrameOpContext context =
                        ref operations.GetContext(opIndex);
                    ulong descriptorKey = ComputeReusableComputeDescriptorBindingKey(
                        in dispatch,
                        in header,
                        in context,
                        operations.Stream.Lane,
                        ResolveComputeDispatchOccurrenceOrdinal(operations.Stream, opIndex));
                    RecordComputeDispatchPayload(
                        secondaryCommandBuffer,
                        imageIndex,
                        in dispatch,
                        descriptorKey);
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                    _commandRuntime.RecordComputeDispatchIndirectPayload(secondaryCommandBuffer, imageIndex, in operations.GetComputeDispatchIndirect(opIndex));
                    break;
                case EVulkanPrimaryPlanNodeKind.MemoryBarrier:
                    EmitMemoryBarrierMask(secondaryCommandBuffer, operations.GetMemoryBarrier(opIndex).Mask);
                    break;
                case EVulkanPrimaryPlanNodeKind.BufferCopy:
                    _commandRuntime.RecordBufferCopyPayload(secondaryCommandBuffer, in operations.GetBufferCopy(opIndex));
                    break;
                case EVulkanPrimaryPlanNodeKind.Query when operations.GetQuery(opIndex).Operation == ERenderQueryOperation.CopyResults:
                    ref readonly QueryPayload query = ref operations.GetQuery(opIndex);
                    if (query.Query.CopyResults(
                            secondaryCommandBuffer,
                            query.ResultDestination,
                            query.ResultDestinationOffset,
                            query.ResultStride,
                            query.IncludeAvailability) !=
                        ERenderQueryReadStatus.Ready)
                    {
                        throw new InvalidOperationException(
                            "A prevalidated Vulkan query result copy became invalid during secondary recording.");
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Frame operation '{header.OpCode}' is not supported by the non-graphics secondary recorder.");
            }
        }

    }
}
