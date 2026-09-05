using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Occlusion;

namespace XREngine.Rendering.Vulkan;

/// <summary>Primary recorder dispatch over the sealed, dense frame-operation stream.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private bool RecordPrimaryOperations(scoped ref PrimaryCommandBufferRecordingState recordingState)
    {
        using var mainLoopProfileScope = RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.MainOpLoop");
        for (int operationIndex = 0; operationIndex < recordingState.Ops.Length; operationIndex++)
        {
            ref readonly FrameOperationHeader header = ref recordingState.Ops.GetHeader(operationIndex);
            if (recordingState.PipelineDeferredOperationIndices.Contains(operationIndex))
            {
                VulkanShadowAtlasDiagnostics.RecordPrimaryOperation(
                    EVulkanShadowAtlasFrameOperationReceiptStage.PrimaryDeferredByPlan,
                    header.OpCode,
                    header.PassIndex,
                    recordingState.Ops.GetTarget(operationIndex));
                continue;
            }

            ref readonly VulkanPrimaryPlanNode primaryNode = ref recordingState.PrimaryCommandPlan.GetNode(operationIndex);
            if (primaryNode.OperationIndex != operationIndex)
                throw new VulkanPlanPreconditionException("A terminal or mismatched primary-plan node appeared in the frame-operation range.");

            try
            {
                VulkanShadowAtlasDiagnostics.RecordPrimaryOperation(
                    EVulkanShadowAtlasFrameOperationReceiptStage.PrimaryAdmission,
                    header.OpCode,
                    header.PassIndex,
                    recordingState.Ops.GetTarget(operationIndex));
                if (!header.RequiresPrimaryRecordingContext)
                {
                    using (VulkanCpuStageScope dispatchStage =
                           new(
                               _frameTelemetry,
                               header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw
                                   ? EVulkanCpuStage.PrimaryMeshOperation
                                   : EVulkanCpuStage.PrimaryNonMeshOperation))
                    {
                        operationIndex = RecordTypedPrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex);
                    }
                    if (recordingState.CommandChainPublicationDeferred)
                        return false;
                    continue;
                }

                int passIndex;
                using (VulkanCpuStageScope preparationStage =
                       new(_frameTelemetry, EVulkanCpuStage.PrimaryOperationPreparation))
                {
                    if (!TryPreparePrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex, out passIndex))
                        continue;
                }

                using (VulkanCpuStageScope dispatchStage =
                       new(
                           _frameTelemetry,
                           header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw
                               ? EVulkanCpuStage.PrimaryMeshOperation
                               : EVulkanCpuStage.PrimaryNonMeshOperation))
                {
                    operationIndex = RecordTypedPrimaryOperation(ref recordingState, in primaryNode, in header, operationIndex, passIndex);
                }
                if (recordingState.CommandChainPublicationDeferred)
                    return false;
            }
            catch (Exception exception)
            {
                if (exception is VulkanPlanPreconditionException)
                    throw;
                HandlePrimaryOperationRecordingFailure(ref recordingState, in header, operationIndex, exception);
            }
        }
        return true;
    }

    private int RecordTypedPrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int index, int passIndex = int.MinValue)
    {
        int resolvedPass = passIndex == int.MinValue ? header.PassIndex : passIndex;
        VulkanPrimaryOperationRecordingInfo info = new(node.Actions, index, resolvedPass);
        if (info.EndsRendering && state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        RecordVulkanCommandDiagnosticMarker(state.CommandBuffer, header.OpCode, resolvedPass, index);
        if (TryRecordPlannedNonGraphicsSecondaryRange(
                ref state,
                in header,
                in info,
                out int lastSecondaryOperationIndex))
        {
            return lastSecondaryOperationIndex;
        }

        return header.OpCode switch
        {
            EVulkanPrimaryPlanNodeKind.TextureUpload => RecordTextureUploadPayload(ref state, in state.Ops.GetTextureUpload(index), in info),
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => RecordMemoryBarrierPayload(ref state, in state.Ops.GetMemoryBarrier(index), in info),
            EVulkanPrimaryPlanNodeKind.SubmissionMarker => RecordSubmissionMarkerPayload(ref state, in state.Ops.GetSubmissionMarker(index), in info),
            EVulkanPrimaryPlanNodeKind.BufferCopy => RecordBufferCopyPayload(ref state, in state.Ops.GetBufferCopy(index), in info),
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => RecordComputeDispatchPayload(ref state, in state.Ops.GetComputeDispatch(index), in info),
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => RecordComputeDispatchIndirectPayload(ref state, in state.Ops.GetComputeDispatchIndirect(index), in info),
            EVulkanPrimaryPlanNodeKind.Query => RecordQueryPayload(ref state, in state.Ops.GetQuery(index), in info),
            EVulkanPrimaryPlanNodeKind.TransformFeedback => RecordTransformFeedbackPayload(ref state, in state.Ops.GetTransformFeedback(index), in info),
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount => RecordMeshTaskPayload(ref state, in state.Ops.GetMeshTask(index), in info),
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling => RecordPublishFramebufferPayload(ref state, in state.Ops.GetPublishedFramebuffer(index), in info),
            EVulkanPrimaryPlanNodeKind.DlssUpscale => RecordDlssUpscalePayload(ref state, in state.Ops.GetDlssUpscale(index), in info),
            EVulkanPrimaryPlanNodeKind.DlssFrameGeneration => RecordDlssFrameGenerationPayload(ref state, in state.Ops.GetDlssFrameGeneration(index), in info),
            EVulkanPrimaryPlanNodeKind.AdvancedVisibility => RecordAdvancedVisibilityPayload(ref state, in state.Ops.GetAdvancedVisibility(index), in info),
            EVulkanPrimaryPlanNodeKind.MeshDraw => RecordMeshDrawPayload(ref state, in state.Ops.GetMeshDraw(index), in info),
            EVulkanPrimaryPlanNodeKind.IndirectDraw => RecordIndirectDrawPayload(ref state, in state.Ops.GetIndirectDraw(index), in info),
            EVulkanPrimaryPlanNodeKind.Clear => RecordClearPayload(ref state, in state.Ops.GetClear(index), in info),
            EVulkanPrimaryPlanNodeKind.Blit => RecordBlitPayload(ref state, in state.Ops.GetBlit(index), in info),
            _ => throw new VulkanPlanPreconditionException($"Typed primary dispatch for '{header.OpCode}' has not been published."),
        };
    }

    /// <summary>
    /// Records the first production advanced-visibility slice. The sequence
    /// is deliberately one opcode so the sealed operation cannot expose an
    /// intermediate count to the CPU: EarlyVisibility writes the GPU-owned
    /// visible list, the compute barrier publishes it, then
    /// BuildVisibilityIndirect writes GPU-consumed indirect arguments.
    /// </summary>
    private int RecordAdvancedVisibilityPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
        => (payload.Request.Stage, payload.Request.Phase) switch
        {
            (EAdvancedRenderStage.VisibilityPreparation, EAdvancedVisibilityStageBackendPhase.Complete) =>
                RecordAdvancedVisibilityPreparationPayload(
                    ref state,
                    in payload,
                    in info),
            (EAdvancedRenderStage.VisibilityRaster, EAdvancedVisibilityStageBackendPhase.Complete) =>
                RecordAdvancedVisibilityRasterPayload(
                    ref state,
                    in payload,
                    in info),
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateCompute) =>
                RecordAdvancedVisibilityLateComputePayload(ref state, in payload, in info),
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility, EAdvancedVisibilityStageBackendPhase.LateRaster) =>
                RecordAdvancedVisibilityLateRasterPayload(ref state, in payload, in info),
            (EAdvancedRenderStage.WorkClassification, EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.AmbientOcclusion, EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.NativeOpaqueShading, EAdvancedVisibilityStageBackendPhase.Complete) =>
                RecordAdvancedNativeComputePayload(ref state, in payload, in info),
            _ => throw new VulkanPlanPreconditionException(
                $"Advanced visibility stage '{payload.Request.Stage}' phase '{payload.Request.Phase}' is outside the admitted physical family."),
        };

    private int RecordAdvancedVisibilityPreparationPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (!payload.State.IsValid || !payload.SceneState.IsValid)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced visibility operation reached native recording without admitted set-1 and canonical scene states.");
        }
        if (payload.EarlyVisibilityProgram is not { IsLinked: true } early ||
            payload.BuildIndirectProgram is not { IsLinked: true } indirect ||
            early.LinkGeneration != payload.EarlyVisibilityLinkGeneration ||
            indirect.LinkGeneration != payload.BuildIndirectLinkGeneration ||
            payload.EarlyVisibilityPipeline.Handle == 0 ||
            payload.BuildIndirectPipeline.Handle == 0)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced visibility compute pipeline closure changed after frame-plan sealing.");
        }

        if (payload.State.ViewCount != (uint)payload.Request.Views.ViewCount)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced visibility preparation has a view-set/state cardinality mismatch.");
        }

        uint groups = DivideRoundUp(payload.State.PayloadCapacity, 256u);
        if (groups == 0u)
            throw new VulkanPlanPreconditionException(
                "Advanced visibility preparation reached recording with an empty payload capacity.");

        for (uint viewIndex = 0u; viewIndex < payload.State.ViewCount; ++viewIndex)
        {
            if (!payload.State.TryGetViewSegment(viewIndex, out uint payloadBase, out uint rangeBase))
                throw new VulkanPlanPreconditionException("Advanced visibility could not resolve a sealed early view segment.");

            CmdBeginLabel(state.CommandBuffer, "Advanced.Visibility.Early");
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.EarlyVisibilityPipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, early.PipelineLayout, in payload);
            PushConstantsTracked(state.CommandBuffer, early.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                    DeviceContext), 0u,
                new AdvancedVisibilityPreparationPushConstants(
                    viewIndex, payloadBase, payload.State.PayloadCapacity, rangeBase));
            Api.CmdDispatch(state.CommandBuffer, groups, 1u, 1u);
            CmdEndLabel(state.CommandBuffer);
        }

        CmdBeginLabel(state.CommandBuffer, "Advanced.Visibility.EarlyToIndirect");
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);
        CmdEndLabel(state.CommandBuffer);

        for (uint viewIndex = 0u; viewIndex < payload.State.ViewCount; ++viewIndex)
        {
            if (!payload.State.TryGetViewSegment(viewIndex, out uint payloadBase, out uint rangeBase))
                throw new VulkanPlanPreconditionException("Advanced visibility could not resolve a sealed indirect view segment.");

            CmdBeginLabel(state.CommandBuffer, "Advanced.Visibility.BuildIndirect");
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.BuildIndirectPipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, indirect.PipelineLayout, in payload);
            PushConstantsTracked(state.CommandBuffer, indirect.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                    DeviceContext), 0u,
                new AdvancedVisibilityPreparationPushConstants(
                    viewIndex, payloadBase, payload.State.PayloadCapacity, rangeBase));
            Api.CmdDispatch(state.CommandBuffer, groups, 1u, 1u);
            CmdEndLabel(state.CommandBuffer);
        }
        return info.OperationIndex;
    }

    private unsafe int RecordAdvancedVisibilityLateComputePayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            payload.LateTargetClosure is not { IsRecordingReady: true } closure ||
            payload.BuildDepthPyramidProgram is not { IsLinked: true } depth ||
            payload.LateVisibilityProgram is not { IsLinked: true } late ||
            depth.LinkGeneration != payload.BuildDepthPyramidLinkGeneration ||
            late.LinkGeneration != payload.LateVisibilityLinkGeneration ||
            payload.BuildDepthPyramidPipeline.Handle == 0 ||
            payload.LateVisibilityPipeline.Handle == 0)
        {
            throw new VulkanPlanPreconditionException("Advanced late visibility reached recording without its sealed compute, image, and descriptor closure.");
        }
        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        for (uint viewIndex = 0u; viewIndex < payload.State.ViewCount; ++viewIndex)
        {
            if (!payload.State.TryGetViewSegment(viewIndex, out uint payloadBase, out uint rangeBase))
                throw new VulkanPlanPreconditionException("Advanced late visibility could not resolve a sealed view segment.");
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer, closure.DepthGroup, 0u, viewIndex,
                ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit,
                allowUndefined: false);
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer, closure.PyramidGroup, 0u, viewIndex,
                ImageLayout.General,
                AccessFlags.ShaderWriteBit,
                PipelineStageFlags.ComputeShaderBit,
                allowUndefined: true);

            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.BuildDepthPyramidPipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, depth.PipelineLayout, in payload,
                closure.DescriptorSets![closure.DescriptorIndex(viewIndex, 0)]);
            PushConstantsTracked(state.CommandBuffer, depth.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                    DeviceContext), 0u,
                new AdvancedVisibilityPreparationPushConstants(
                    viewIndex, payloadBase, payload.State.PayloadCapacity, rangeBase));
            long buildRecordStart = Stopwatch.GetTimestamp();
            using (VulkanGpuProfilerScope gpuScope = TryBeginVulkanGpuProfilerScope(
                       state.CommandBuffer, NativeHiZBuildGpuProfilerPath))
            {
                Api.CmdDispatch(state.CommandBuffer,
                    DivideRoundUp(closure.DepthGroup.ResolvedExtent.Width, 64u),
                    DivideRoundUp(closure.DepthGroup.ResolvedExtent.Height, 64u), 1u);
            }
            OcclusionTelemetry.RecordHiZBuild(
                closure.DepthGroup.ResolvedExtent.Width,
                closure.DepthGroup.ResolvedExtent.Height,
                Stopwatch.GetElapsedTime(buildRecordStart).TotalMilliseconds);
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer, closure.PyramidGroup, 0u, viewIndex,
                ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit,
                allowUndefined: false);

            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute,
                payload.LateVisibilityPipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer,
                PipelineBindPoint.Compute, late.PipelineLayout, in payload,
                closure.DescriptorSets![closure.DescriptorIndex(viewIndex, 1)]);
            PushConstantsTracked(state.CommandBuffer, late.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                    DeviceContext), 0u,
                new AdvancedVisibilityPreparationPushConstants(
                    viewIndex, payloadBase, payload.State.PayloadCapacity, rangeBase));
            long testRecordStart = Stopwatch.GetTimestamp();
            using (VulkanGpuProfilerScope gpuScope = TryBeginVulkanGpuProfilerScope(
                       state.CommandBuffer, NativeHiZTestGpuProfilerPath))
            {
                Api.CmdDispatch(state.CommandBuffer, DivideRoundUp(payload.State.PayloadCapacity, 256u), 1u, 1u);
            }
            OcclusionTelemetry.RecordHiZTest(
                payload.State.PayloadCapacity,
                Stopwatch.GetElapsedTime(testRecordStart).TotalMilliseconds);
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderStorage);
        }

        // The graph transition into LateRaster owns the attachment layout.
        // This physical compute pass exits with the produced coarse tile
        // level in General, matching its declared read/write storage use rather than
        // leaking the internal sampled layout across the pass boundary.
        for (uint viewIndex = 0u; viewIndex < payload.State.ViewCount; ++viewIndex)
            EmitAdvancedVisibilityImageBarrier(
                state.CommandBuffer,
                closure.PyramidGroup,
                0u,
                viewIndex,
                ImageLayout.General,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                PipelineStageFlags.ComputeShaderBit,
                allowUndefined: false);
        return info.OperationIndex;
    }

    private int RecordAdvancedVisibilityLateRasterPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.Request.Phase != EAdvancedVisibilityStageBackendPhase.LateRaster)
            throw new VulkanPlanPreconditionException(
                "Late raster recording received a non-raster physical phase.");

        VulkanAdvancedVisibilityResourceState lateState = payload.State with
        {
            RangeCounts = payload.State.LateRangeCounts,
            IndirectArguments = payload.State.LateIndirectArguments,
            MeshArguments = payload.State.LateMeshArguments,
            MeshPayloads = payload.State.LateMeshPayloads,
        };
        VulkanAdvancedVisibilityOperationPayload latePayload = payload with
        {
            State = lateState,
        };
        return RecordAdvancedVisibilityRasterPayload(ref state, in latePayload, in info);
    }

    private unsafe void EmitAdvancedVisibilityImageBarrier(
        CommandBuffer commandBuffer,
        VulkanPhysicalImageGroup group,
        uint mipLevel,
        uint arrayLayer,
        ImageLayout nextLayout,
        AccessFlags destinationAccess,
        PipelineStageFlags destinationStage,
        bool allowUndefined)
    {
        ImageAspectFlags aspect = group.Format switch
        {
            Format.D16UnormS8Uint or Format.D24UnormS8Uint or
                Format.D32SfloatS8Uint =>
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            Format.D16Unorm or Format.D32Sfloat =>
                ImageAspectFlags.DepthBit,
            _ => ImageAspectFlags.ColorBit,
        };
        ImageSubresourceRange range = new(
            aspect,
            mipLevel,
            1u,
            arrayLayer,
            1u);
        ImageLayout oldLayout = TryGetRecordedImageAccessState(
                commandBuffer,
                group.Image,
                in range,
                out VulkanImageAccessState recordedState,
                includeEntryState: true,
                includeUndefinedState: allowUndefined)
            ? recordedState.Layout
            : group.GetKnownLayout(mipLevel, 1u, arrayLayer, 1u);
        if (oldLayout == ImageLayout.Undefined && !allowUndefined)
            throw new VulkanPlanPreconditionException(
                "A late-visibility source subresource has no exact tracked layout.");
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = nextLayout,
            SrcAccessMask = SourceAccessForAdvancedVisibilityLayout(oldLayout),
            DstAccessMask = destinationAccess,
            Image = group.Image,
            SubresourceRange = range,
        };
        CmdPipelineBarrierTracked(commandBuffer,
            PipelineStageFlags.AllCommandsBit, destinationStage,
            DependencyFlags.None, 0, null, 0, null, 1, &barrier);
    }

    private static AccessFlags SourceAccessForAdvancedVisibilityLayout(
        ImageLayout layout) => layout switch
        {
            ImageLayout.Undefined => 0u,
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
            ImageLayout.General => AccessFlags.ShaderReadBit |
                                   AccessFlags.ShaderWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal =>
                AccessFlags.DepthStencilAttachmentReadBit |
                AccessFlags.DepthStencilAttachmentWriteBit,
            _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };

    private unsafe int RecordAdvancedVisibilityRasterPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        FramePlan framePlan = state.FramePlan
            ?? throw new VulkanPlanPreconditionException(
                "Advanced visibility raster reached recording without an accepted frame plan.");
        VulkanPreparedStableBinStream bins = framePlan.StableBins;
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            !payload.TargetClosure.IsValid || !bins.HasSealedSubmissionPlans ||
            payload.Request.Views.ViewCount <= 0 || !info.BeginsRendering)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced visibility raster reached recording without its sealed view-set resource, target, and stable-bin closure.");
        }
        VulkanVisibilityPreparedVertexSource currentDeformation =
            payload.State.Geometry.CurrentVertices;
        VulkanVisibilityPreparedVertexSource previousDeformation =
            payload.State.Geometry.PreviousVertices;
        string currentDeformationReason = "Ready";
        string previousDeformationReason = "Ready";
        bool currentDeformationValid =
            currentDeformation.TryValidate(
                ResourceRuntime,
                out currentDeformationReason);
        bool previousDeformationValid =
            previousDeformation.TryValidate(
                ResourceRuntime,
                out previousDeformationReason);
        if (!currentDeformationValid || !previousDeformationValid)
        {
            throw new VulkanPlanPreconditionException(
                $"Advanced visibility deformation buffers changed after sealing: current={currentDeformationReason}, previous={previousDeformationReason}.");
        }
        if (ResourceRuntime.BackendObjects.Get(payload.Request.Target) is not
                VkFrameBuffer targetWrapper)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced visibility raster lost its authoritative Vulkan framebuffer wrapper.");
        }
        // The resize recorder accepts only a frozen target closure. Refreshing
        // it here could publish replacement internal images after admission.
        if (state.Policy.AllowSynchronousResourceUploads)
            targetWrapper.EnsureCurrent();
        if (!targetWrapper.TryCaptureRecordedRenderTargetSnapshot(
                out VulkanRecordedRenderTargetSnapshot currentTarget) ||
            currentTarget != payload.TargetClosure.NativeTarget)
        {
            string mismatch = currentTarget.IsComplete
                ? payload.TargetClosure.NativeTarget.DescribeFirstMismatch(
                    in currentTarget)
                : "the current native target is incomplete";
            throw new VulkanPlanPreconditionException(
                $"Advanced visibility raster target changed after sealing: {mismatch}.");
        }

        // The preparation family owns the indirect arguments and range counts.
        // Publish all of its writes once, outside rendering, before any stable
        // bin binds or indirect-count reads.
        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        EmitAdvancedVisibilityRasterReadBarrier(ref state);
        BeginRenderPassForTarget(
            ref state,
            payload.Request.Target,
            info.PassIndex,
            state.ActiveContext,
            clearPolicy: payload.TargetClosure.ClearPolicy);
        if (!state.RenderScope.IsActive ||
            state.RenderScope.Target != payload.TargetClosure.Target ||
            state.RenderScope.UsesDynamicRendering !=
                payload.TargetClosure.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly !=
                payload.TargetClosure.DepthStencilReadOnly ||
            payload.TargetClosure.UsesDynamicRendering &&
                !state.RenderScope.DynamicRenderingFormats.Equals(
                    payload.TargetClosure.DynamicRenderingFormats) ||
            !payload.TargetClosure.UsesDynamicRendering &&
                state.RenderScope.RenderPass.Handle !=
                    payload.TargetClosure.RenderPass.Handle)
        {
            throw new VulkanPlanPreconditionException(
                "The active visibility render scope does not match its sealed target compatibility closure.");
        }

        Extent2D extent = new(
            payload.TargetClosure.NativeTarget.Width,
            payload.TargetClosure.NativeTarget.Height);
        Viewport viewport = CreateVulkanViewport(extent);
        Rect2D scissor = new(new Offset2D(0, 0), extent);
        SetViewportScissorTracked(state.CommandBuffer, in viewport, in scissor);

        CmdBeginLabel(state.CommandBuffer, "Advanced.Visibility.Raster");
        ReadOnlySpan<VulkanPreparedStableBinHeader> headers = bins.Headers;
        ReadOnlySpan<VulkanPreparedStableBinRecord> allRecords = bins.Records;
        for (uint viewIndex = 0u;
             viewIndex < (uint)payload.Request.Views.ViewCount;
             ++viewIndex)
        {
            // Canonical set-1 retains every logical view. Raster is issued once
            // per view so graphics shaders receive the exact ViewId rather than
            // silently projecting all bins with view zero. Target-family routing
            // remains frozen in TargetClosure; this loop never changes strategy.
            for (int headerIndex = 0; headerIndex < headers.Length; ++headerIndex)
            {
                ref readonly VulkanPreparedStableBinHeader header =
                    ref headers[headerIndex];
            if (!header.IsRasterReady)
            {
                throw new VulkanPlanPreconditionException(
                    "A non-empty visibility bin has no sealed raster pipeline and submission plan.");
            }

            VulkanVisibilityRasterPipeline raster = header.RasterPipeline;
            if (!raster.IsValid || raster.TargetClosure != payload.TargetClosure ||
                raster.Program.LinkGeneration != raster.ProgramLinkGeneration)
            {
                throw new VulkanPlanPreconditionException(
                    "A visibility raster pipeline closure changed after frame-plan sealing.");
            }
            VulkanSealedBinSubmissionPlan plan = header.SubmissionPlan!;
            if (!plan.OutputPolicy.AllowsCanonicalVisibilityFamily)
            {
                throw new VulkanPlanPreconditionException(
                    $"Advanced visibility raster reached recording with an unsupported output policy: {plan.OutputPolicy.DescribeCanonicalVisibilityRejection()}.");
            }
            // LateVisibility deliberately excludes CPU-direct producers: they
            // have no GPU-owned recovered-count stream and replaying them here
            // would duplicate early raster work.
            if (payload.Request.Phase == EAdvancedVisibilityStageBackendPhase.LateRaster &&
                plan.ResolvedStrategy == EMeshSubmissionStrategy.CpuDirect)
            {
                continue;
            }
            AdvancedIndirectRange indirectRange = header.IndirectRange;
            VulkanAdvancedVisibilityResourceState visibilityState =
                payload.State;
            if (!visibilityState.TryGetViewSegment(
                    viewIndex,
                    out uint payloadBase,
                    out uint rangeBase))
            {
                throw new VulkanPlanPreconditionException(
                    "Stable visibility raster could not resolve its sealed view segment.");
            }
            if (plan.ResolvedStrategy != EMeshSubmissionStrategy.CpuDirect)
            {
                try
                {
                    // Lowering remains range-based, but all GPU-produced
                    // command/count streams are sealed as contiguous per-view
                    // segments. CPU-direct records retain their canonical
                    // source payload indices and are issued once per view.
                    indirectRange = indirectRange with
                    {
                        FirstPayloadIndex = checked(indirectRange.FirstPayloadIndex + payloadBase),
                        ArgumentBufferOffset = checked(indirectRange.ArgumentBufferOffset +
                            payloadBase * 20u),
                        CountBufferOffset = checked(indirectRange.CountBufferOffset +
                            rangeBase * sizeof(uint)),
                    };
                }
                catch (OverflowException)
                {
                    throw new VulkanPlanPreconditionException(
                        "Stable visibility raster view segment overflowed the sealed set-1 ABI.");
                }
            }
            if (!VulkanStableBinSubmissionLowering.TryLower(
                    plan,
                    in header,
                    in indirectRange,
                    in visibilityState,
                    out VulkanStableBinSubmission submission,
                    out VulkanStableBinSubmissionLoweringFailure lowerFailure))
            {
                throw new VulkanPlanPreconditionException(
                    $"Stable visibility bin lowering failed after sealing: {lowerFailure}.");
            }

            BindPipelineTracked(
                state.CommandBuffer,
                PipelineBindPoint.Graphics,
                raster.Pipeline);
            BindAdvancedVisibilityDescriptorSets(
                state.CommandBuffer,
                PipelineBindPoint.Graphics,
                raster.PipelineLayout,
                in payload);

            VulkanResidentDrawTemplateNativeState native = header.NativeState;
            ReadOnlySpan<VulkanPreparedStableBinRecord> records =
                allRecords.Slice(header.RecordOffset, header.RecordCount);
            bool meshlet = raster.IsMeshShaderPipeline;
            if (native.PrimitiveCount != 1 || native.Primitive0.Topology !=
                PrimitiveTopology.TriangleList || (!meshlet &&
                (!native.Primitive0.Indexed || native.VertexBufferCount != 1 ||
                 native.GetVertexBinding(0) != 0u ||
                 native.Primitive0.IndexBuffer.Handle == 0)))
            {
                throw new VulkanPlanPreconditionException(
                    "A visibility raster bin has an invalid canonical packed-geometry closure.");
            }
            VulkanVisibilityGeometryRecordClosure geometryClosure =
                records[0].VisibilityGeometryClosure;
            VulkanAdvancedScenePublicationState sealedSceneState =
                payload.SceneState;
            for (int recordIndex = 0; recordIndex < records.Length; ++recordIndex)
            {
                VulkanVisibilityGeometryRecordClosure recordClosure =
                    records[recordIndex].VisibilityGeometryClosure;
                string closureReason =
                    "the geometry range does not share its bin publication";
                if (recordClosure.PreparedVertexSource !=
                        geometryClosure.PreparedVertexSource ||
                    recordClosure.IndexSlice != geometryClosure.IndexSlice ||
                    !recordClosure.TryValidate(
                        ResourceRuntime,
                        in sealedSceneState,
                        out closureReason))
                {
                    throw new VulkanPlanPreconditionException(closureReason);
                }
            }
            if (!meshlet)
            {
                for (int bindingIndex = 0;
                     bindingIndex < native.VertexBufferCount;
                     ++bindingIndex)
                {
                    BindVertexBufferTracked(
                        state.CommandBuffer,
                        native.GetVertexBinding(bindingIndex),
                        native.GetVertexBuffer(bindingIndex),
                        geometryClosure.PreparedVertexSource.Offset);
                }
                BindIndexBufferTracked(
                    state.CommandBuffer,
                    native.Primitive0.IndexBuffer,
                    geometryClosure.IndexSlice.Offset,
                    native.Primitive0.IndexType);
            }

            if (plan.ResolvedStrategy == EMeshSubmissionStrategy.CpuDirect)
            {
                PushConstantsTracked(
                    state.CommandBuffer,
                    raster.PipelineLayout,
                    VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                        DeviceContext),
                    0u,
                    new AdvancedVisibilityMeshRasterPushConstants(
                        indirectRange.FirstPayloadIndex,
                        (uint)indirectRange.Key.Producer,
                        viewIndex,
                        1u));
                RecordAdvancedVisibilityCpuDirect(
                    state.CommandBuffer,
                    records);
                continue;
            }
            if (meshlet)
            {
                PushConstantsTracked(
                    state.CommandBuffer,
                    raster.PipelineLayout,
                    VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                        DeviceContext),
                    0u,
                    new AdvancedVisibilityMeshRasterPushConstants(
                        indirectRange.FirstPayloadIndex,
                        (uint)indirectRange.Key.Producer,
                        viewIndex,
                        1u));
            }
            else
            {
                PushConstantsTracked(
                    state.CommandBuffer,
                    raster.PipelineLayout,
                    VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(
                        DeviceContext),
                    0u,
                    new AdvancedVisibilityMeshRasterPushConstants(
                        indirectRange.FirstPayloadIndex,
                        (uint)indirectRange.Key.Producer,
                        viewIndex,
                        1u));
            }
            if (!TryRecordStableBinSubmission(
                    state.CommandBuffer,
                    in submission,
                    null,
                    records,
                    out VulkanStableBinSubmissionRecordingFailure recordFailure))
            {
                throw new VulkanPlanPreconditionException(
                    $"Stable visibility bin recording failed after sealing: {recordFailure}.");
            }

            // Diagnostic plans are part of the sealed lane. Their copy stays
            // in this producer primary, after the indirect/raster consumer,
            // and uses the same completion authority as the frame.
            if (viewIndex == 0u && plan.DiagnosticPlan is { } diagnosticPlan &&
                AdvancedVisibilityDiagnosticCopy is { } recordDiagnosticCopy &&
                !recordDiagnosticCopy(
                    state.CommandBuffer,
                    in visibilityState,
                    in diagnosticPlan,
                    payload.Request.RenderFrameId))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.AdvancedVisibility.DiagnosticDrop.{diagnosticPlan.PassIdentity}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Dropped sealed advanced-visibility diagnostic sidecar for pass {0}.",
                    diagnosticPlan.PassIdentity);
            }
            if (viewIndex == 0u && plan.OverflowDiagnosticPlan is { } overflowDiagnosticPlan &&
                AdvancedVisibilityDiagnosticCopy is { } recordOverflowDiagnosticCopy &&
                !recordOverflowDiagnosticCopy(
                    state.CommandBuffer,
                    in visibilityState,
                    in overflowDiagnosticPlan,
                    payload.Request.RenderFrameId))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.AdvancedVisibility.OverflowDiagnosticDrop.{overflowDiagnosticPlan.PassIdentity}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Dropped sealed advanced-visibility overflow diagnostic sidecar for pass {0}.",
                    overflowDiagnosticPlan.PassIdentity);
            }
            }
        }
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private unsafe void EmitAdvancedVisibilityRasterReadBarrier(
        scoped ref PrimaryCommandBufferRecordingState state)
    {
        MemoryBarrier memoryBarrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit |
                AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.IndirectCommandReadBit |
                AccessFlags.ShaderReadBit |
                AccessFlags.ShaderWriteBit,
        };
        PipelineStageFlags destinationStages = PipelineStageFlags.DrawIndirectBit |
            PipelineStageFlags.VertexShaderBit |
            PipelineStageFlags.FragmentShaderBit;
        if (DeviceContext.SupportsMeshTaskIndirectCount)
            destinationStages |= PipelineStageFlags.MeshShaderBitExt;
        CmdPipelineBarrierTracked(
            state.CommandBuffer,
            PipelineStageFlags.ComputeShaderBit |
                PipelineStageFlags.TransferBit,
            destinationStages,
            DependencyFlags.None,
            1,
            &memoryBarrier,
            0,
            null,
            0,
            null);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(
            emittedCount: 1,
            redundantCount: 0);
    }

    private unsafe void RecordAdvancedVisibilityCpuDirect(
        CommandBuffer commandBuffer,
        ReadOnlySpan<VulkanPreparedStableBinRecord> records)
    {
        for (int index = 0; index < records.Length; ++index)
        {
            VulkanPreparedVisibilityDirectDraw draw =
                records[index].VisibilityDirectDraw;
            if (!draw.IsValid)
                throw new VulkanPlanPreconditionException(
                    "A CPU-direct visibility bin contains invalid frozen indexed arguments.");
            Api!.CmdDrawIndexed(
                commandBuffer,
                draw.IndexCount,
                draw.InstanceCount,
                draw.FirstIndex,
                draw.VertexOffset,
                draw.FirstInstance);
        }
    }

    private void BindAdvancedVisibilityDescriptorSets(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        in VulkanAdvancedVisibilityOperationPayload payload)
    {
        // Set 0 is the standard engine uniform tier and is intentionally not
        // synthesized here. This compute-only shader pair has no set-0
        // descriptor dependency. Sets 1/2/3 are the exact externally owned
        // visibility, resource, and canonical-scene publications captured by
        // the sealed frame plan.
        Span<DescriptorSet> sets = stackalloc DescriptorSet[3]
        {
            payload.State.DescriptorSet,
            payload.SceneState.ResourceDescriptorSet,
            payload.SceneState.GlobalDescriptorSet,
        };
        BindDescriptorSetsTracked(
            commandBuffer,
            bindPoint,
            layout,
            VulkanAdvancedSceneProgramBindingContract.VisibilitySetIndex,
            sets,
            ReadOnlySpan<uint>.Empty);
    }

    private void BindAdvancedVisibilityDescriptorSets(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        in VulkanAdvancedVisibilityOperationPayload payload,
        DescriptorSet visibilitySet)
    {
        Span<DescriptorSet> sets = stackalloc DescriptorSet[3]
        {
            visibilitySet,
            payload.SceneState.ResourceDescriptorSet,
            payload.SceneState.GlobalDescriptorSet,
        };
        BindDescriptorSetsTracked(commandBuffer, bindPoint, layout,
            VulkanAdvancedSceneProgramBindingContract.VisibilitySetIndex, sets,
            ReadOnlySpan<uint>.Empty);
    }

    private static uint DivideRoundUp(uint value, uint divisor)
        => checked((value + divisor - 1u) / divisor);

    private readonly record struct AdvancedVisibilityMeshRasterPushConstants(
        uint MeshArgumentBase,
        uint ProducerAndOrigin,
        uint ViewIndex,
        uint Flags);

    private readonly record struct AdvancedVisibilityPreparationPushConstants(
        uint ViewIndex,
        uint PayloadBase,
        uint PayloadCapacity,
        uint RangeBase);

    /// <summary>
    /// Executes a planned non-graphics secondary range. The dense frame-op
    /// migration retained secondary buckets and plan actions, so this bridge is
    /// the authoritative consumer that keeps those publications reachable.
    /// </summary>
    private bool TryRecordPlannedNonGraphicsSecondaryRange(
        scoped ref PrimaryCommandBufferRecordingState state,
        in FrameOperationHeader header,
        in VulkanPrimaryOperationRecordingInfo info,
        out int lastOperationIndex)
    {
        lastOperationIndex = info.OperationIndex;
        if (!info.ExecutesSecondaryRange ||
            header.OpCode is not (
                EVulkanPrimaryPlanNodeKind.ComputeDispatch or
                EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
                EVulkanPrimaryPlanNodeKind.BufferCopy or
                EVulkanPrimaryPlanNodeKind.MemoryBarrier or
                EVulkanPrimaryPlanNodeKind.Query) ||
            !TryGetSecondaryBucketForStart(
                state.SecondaryBuckets,
                state.SecondaryBucketByStart,
                info.OperationIndex,
                out VulkanSecondaryRecordingBucket bucket))
        {
            return false;
        }

        string label = header.OpCode switch
        {
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => "ComputeDispatch",
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect =>
                state.Ops.GetComputeDispatchIndirect(info.OperationIndex).Label,
            EVulkanPrimaryPlanNodeKind.BufferCopy =>
                state.Ops.GetBufferCopy(info.OperationIndex).Label,
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => "MemoryBarrier",
            EVulkanPrimaryPlanNodeKind.Query =>
                $"Query.{state.Ops.GetQuery(info.OperationIndex).Operation}",
            _ => throw new VulkanPlanPreconditionException(
                $"Unsupported non-graphics secondary range kind '{header.OpCode}'."),
        };

        bool barrierPlanHasPass =
            state.RenderGraphPlan.CompiledGraph.Plan.Execution.TryGetPassOrder(
                info.PassIndex,
                out _) ||
            info.PassIndex == VulkanBarrierPlanner.SwapchainPassIndex;
        if (!TryRecordSecondaryBucket(
                primaryCommandBuffer: state.CommandBuffer,
                state.FrameDataImageIndex,
                state.ExecutedCommandChainSecondaryHandles,
                state.Ops,
                state.ScheduledCommandChainKeysByOpIndex,
                state.ScheduledCommandChainCache,
                info.OperationIndex,
                bucket,
                info.PassIndex,
                barrierPlanHasPass,
                state.RenderScope.IsActive,
                state.ActiveInlineQuery is not null,
                label))
        {
            return false;
        }

        lastOperationIndex = info.OperationIndex + bucket.Count - 1;
        return true;
    }

    private int RecordTextureUploadPayload(scoped ref PrimaryCommandBufferRecordingState state, in TextureUploadPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (info.EndsRendering) EndActiveRenderPass(ref state);
        if (state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        CmdBeginLabel(state.CommandBuffer, "TextureUpload");
        RecordTextureUploadOp(state.CommandBuffer, payload.Upload);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordMemoryBarrierPayload(scoped ref PrimaryCommandBufferRecordingState state, in MemoryBarrierPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "MemoryBarrier");
        EmitMemoryBarrierMask(state.CommandBuffer, payload.Mask);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordSubmissionMarkerPayload(scoped ref PrimaryCommandBufferRecordingState state, in SubmissionMarkerPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        RegisterSubmissionMarker(state.CommandBuffer, payload.Fence);
        return info.OperationIndex;
    }

    private int RecordBufferCopyPayload(scoped ref PrimaryCommandBufferRecordingState state, in BufferCopyPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, payload.Label);
        RecordBufferCopyPayload(state.CommandBuffer, in payload);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordComputeDispatchPayload(scoped ref PrimaryCommandBufferRecordingState state, in ComputeDispatchPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "ComputeDispatch");
        EnsureComputeSampledImageLayoutsForDispatch(
            state.CommandBuffer,
            payload.Snapshot,
            state.Policy.AllowSynchronousResourceUploads);
        ref readonly FrameOperationHeader header = ref state.Ops.GetHeader(info.OperationIndex);
        ref readonly FrameOpContext context = ref state.Ops.GetContext(info.OperationIndex);
        ulong descriptorKey = ComputeReusableComputeDescriptorBindingKey(
            in payload,
            in header,
            in context,
            state.Ops.Stream.Lane,
            ResolveComputeDispatchOccurrenceOrdinal(state.Ops.Stream, info.OperationIndex));
        RecordComputeDispatchPayload(
            state.CommandBuffer,
            state.FrameDataImageIndex,
            in payload,
            descriptorKey,
            state.Policy.AllowSynchronousResourceUploads);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordComputeDispatchIndirectPayload(scoped ref PrimaryCommandBufferRecordingState state, in ComputeDispatchIndirectPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, payload.Label);
        EnsureComputeSampledImageLayoutsForDispatch(
            state.CommandBuffer,
            payload.Snapshot,
            state.Policy.AllowSynchronousResourceUploads);
        RecordComputeDispatchIndirectPayload(
            state.CommandBuffer,
            state.FrameDataImageIndex,
            in payload,
            state.Policy.AllowSynchronousResourceUploads);
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.Operation == ERenderQueryOperation.CopyResults &&
            payload.Query.CopyResults(state.CommandBuffer, payload.ResultDestination, payload.ResultDestinationOffset, payload.ResultStride, payload.IncludeAvailability) != ERenderQueryReadStatus.Ready)
            state.FrameOpsRequireRerecordLocal = true;
        else if (payload.Operation == ERenderQueryOperation.WriteTimestamp && state.RecordingScratch.PreparedInlineQueries.Contains(payload.Query))
        {
            // Timestamp epochs are consumed by a successful host read. Keep the metadata
            // for this submission, but make the primary artifact one-shot for the next frame.
            _ = payload.Query.WriteTimestamp(state.CommandBuffer, payload.TimestampStage, payload.PointIndex);
            state.FrameOpsRequireRerecordLocal = true;
        }
        else if (payload.Operation == ERenderQueryOperation.WriteProperties &&
                 (!state.RecordingScratch.PreparedInlineQueries.Contains(payload.Query) || payload.Query.WriteProperties(CreateQueryCommandEncoder(), state.CommandBuffer, payload.SourceHandles.Span) != ERenderQueryReadStatus.Ready))
            state.FrameOpsRequireRerecordLocal = true;
        else if (payload.Operation is ERenderQueryOperation.Begin or ERenderQueryOperation.End)
        {
            XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
            if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
            bool label = CanRecordCommandBufferDebugLabels && CmdBeginLabel(state.CommandBuffer, $"Query.{payload.Operation}");
            if (payload.Operation == ERenderQueryOperation.Begin) BeginInlineQueryPayload(ref state, in payload, info.OperationIndex, info.PassIndex);
            else EndInlineQueryPayload(ref state, in payload, info.OperationIndex, info.PassIndex);
            if (label) CmdEndLabel(state.CommandBuffer);
        }
        return info.OperationIndex;
    }

    private static void BeginInlineQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, int index, int pass)
    {
        VkRenderQuery query = payload.Query;
        if (state.ActiveInlineQuery is not null) { state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer); return; }
        if (state.RecordingScratch.PreparedInlineQueries.Contains(query) && state.RecordingScratch.BegunInlineQueries.Add(query))
        {
            state.ActiveInlineQuery = query.BeginQuery(state.CommandBuffer) == ERenderQueryReadStatus.Ready ? query : null;
            if (state.ActiveInlineQuery is null) state.FrameOpsRequireRerecordLocal = true;
            state.ActiveInlineQueryRecordedDraw = false;
            return;
        }
        if (state.RecordingScratch.PreparedInlineQueries.Contains(query)) state.FrameOpsRequireRerecordLocal = true;
    }

    private static void EndInlineQueryPayload(scoped ref PrimaryCommandBufferRecordingState state, in QueryPayload payload, int index, int pass)
    {
        VkRenderQuery query = payload.Query;
        if (ReferenceEquals(state.ActiveInlineQuery, query))
        {
            if (!state.ActiveInlineQueryRecordedDraw) { state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer); }
            query.EndQuery(state.CommandBuffer); state.ActiveInlineQuery = null; state.ActiveInlineQueryRecordedDraw = false; return;
        }
        state.FrameOpsRequireRerecordLocal = true; query.InvalidateRecordedResultEpoch(state.CommandBuffer);
    }

    private int RecordTransformFeedbackPayload(scoped ref PrimaryCommandBufferRecordingState state, in TransformFeedbackPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
        bool labelActive = CanRecordCommandBufferDebugLabels && CmdBeginLabel(state.CommandBuffer, $"TransformFeedback.{payload.Operation}");
        RecordTransformFeedbackPayload(state.CommandBuffer, in payload);
        if (labelActive) CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }

    private int RecordMeshTaskPayload(scoped ref PrimaryCommandBufferRecordingState state, in MeshTaskDispatchIndirectCountPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        // vkCmdPipelineBarrier is not legal while either a legacy render pass or a
        // dynamic-rendering scope is active. The mesh-task command/count buffers
        // are produced by compute or transfer work, so publish their indirect and
        // shader reads before opening the graphics scope that consumes them.
        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        EmitMeshTaskDispatchIndirectCountReadBarrier(ref state);
        if (info.BeginsRendering)
            BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        CmdBeginLabel(state.CommandBuffer, "MeshTaskDispatchIndirectCount");
        RecordMeshTaskDispatchIndirectCountPayload(state.CommandBuffer, state.FrameDataImageIndex, in payload);
        CmdEndLabel(state.CommandBuffer);
        if (target is null)
            state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private unsafe void EmitMeshTaskDispatchIndirectCountReadBarrier(scoped ref PrimaryCommandBufferRecordingState state)
    {
        MemoryBarrier memoryBarrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit,
        };

        PipelineStageFlags destinationStages = PipelineStageFlags.DrawIndirectBit;
        if (DeviceContext.SupportsMeshTaskIndirectCount)
        {
            destinationStages |= PipelineStageFlags.TaskShaderBitExt |
                PipelineStageFlags.MeshShaderBitExt;
        }

        CmdPipelineBarrierTracked(
            state.CommandBuffer,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
            destinationStages,
            DependencyFlags.None,
            1,
            &memoryBarrier,
            0,
            null,
            0,
            null);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount: 1, redundantCount: 0);
    }

    private int RecordPublishFramebufferPayload(scoped ref PrimaryCommandBufferRecordingState state, in PublishFramebufferPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "PublishFramebufferForSampling");
        RecordPublishFramebufferForSamplingPayload(state.CommandBuffer, payload.FrameBuffer);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordDlssUpscalePayload(scoped ref PrimaryCommandBufferRecordingState state, in DlssUpscalePayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "DLSS.SuperResolution");
        RecordDlssUpscalePayload(state.CommandBuffer, in payload);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordDlssFrameGenerationPayload(scoped ref PrimaryCommandBufferRecordingState state, in DlssFrameGenerationPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        CmdBeginLabel(state.CommandBuffer, "DLSS.FrameGenerationInputs");
        RecordDlssFrameGenerationPayload(state.CommandBuffer, state.ImageIndex, in payload);
        CmdEndLabel(state.CommandBuffer); return info.OperationIndex;
    }

    private int RecordMeshDrawPayload(scoped ref PrimaryCommandBufferRecordingState state, in MeshDrawPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (state.CommandChainSchedule is not null &&
            state.ScheduledCommandChainKeysByOpIndex is not null &&
            state.ScheduledCommandChainCache is not null &&
            TryGetScheduledCommandChainForOp(
                ref state,
                info.OperationIndex,
                out _,
                out _))
        {
            int scheduledRunCount = CountContiguousMeshCommandChainRun(
                ref state,
                info.OperationIndex,
                in payload,
                info.PassIndex);
            if (scheduledRunCount > 0 &&
                TryExecuteScheduledMeshCommandChainSecondaryRun(
                    ref state,
                    info.OperationIndex,
                    scheduledRunCount,
                    info.PassIndex))
            {
                if (target is null)
                    state.ActualSwapchainWriteCount += scheduledRunCount;
                return info.OperationIndex + scheduledRunCount - 1;
            }
        }

        if (info.BeginsRendering && !state.RenderScope.MatchesTarget(target))
        {
            EndActiveRenderPass(ref state);
            int slot = GetMeshDrawUniformSlot(ref state, info.OperationIndex, payload.Draw.Renderer, state.ActiveContext, payload.Draw);
            payload.Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(state.CommandBuffer, payload.Draw, slot, state.CommandBufferImageSlot, target, info.PassIndex, state.ActiveContext.PassMetadata);
            BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        }
        int uniformSlot = GetMeshDrawUniformSlot(ref state, info.OperationIndex, payload.Draw.Renderer, state.ActiveContext, payload.Draw);
        bool recorded = RecordMeshDrawPayloadIntoCommandBuffer(ref state, state.CommandBuffer, in payload, target, state.ActiveContext, info.PassIndex, uniformSlot);
        if (state.ActiveInlineQuery is not null && recorded) state.ActiveInlineQueryRecordedDraw = true;
        if (target is null) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private int RecordClearPayload(scoped ref PrimaryCommandBufferRecordingState state, in ClearPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        if (info.BeginsRendering && (!state.RenderScope.IsActive || state.RenderScope.Target != target)) { EndActiveRenderPass(ref state); BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext); }
        uint layers = state.RenderScope.UsesDynamicRendering ? Math.Max(state.RenderScope.DynamicRenderingFormats.LayerCount, 1u) : 0u;
        uint viewMask = state.RenderScope.UsesDynamicRendering ? state.RenderScope.DynamicRenderingFormats.ViewMask : 0u;
        bool recorded = false;
        if (target is null && state.SwapchainClearedThisFrame && payload.ClearColor)
        {
            if (payload.ClearDepth || payload.ClearStencil) { RecordClearPayload(state.CommandBuffer, state.ImageIndex, in payload, target, state.RenderScope.RenderArea, in state.SwapchainTarget, layers, viewMask, true); recorded = true; }
        }
        else { RecordClearPayload(state.CommandBuffer, state.ImageIndex, in payload, target, state.RenderScope.RenderArea, in state.SwapchainTarget, layers, viewMask); recorded = true; }
        if (target is null && recorded) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private int RecordBlitPayload(scoped ref PrimaryCommandBufferRecordingState state, in BlitPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.ColorBit && (payload.InFbo is null || payload.OutFbo is null)) EnsureSwapchainColorAttachmentLayoutForBlit(ref state);
        CmdBeginLabel(state.CommandBuffer, "Blit");
        bool recorded = RecordBlitPayload(state.CommandBuffer, state.ImageIndex, payload, in state.SwapchainTarget, exactColorSource: null);
        CmdEndLabel(state.CommandBuffer);
        if (payload.OutFbo is null && (payload.ColorBit || payload.DepthBit || payload.StencilBit) && recorded) { state.SwapchainWrittenOutsideRenderPass = true; if (payload.ColorBit) { state.SwapchainInColorAttachmentLayout = true; state.SwapchainFinalLayout = ImageLayout.ColorAttachmentOptimal; } state.ActualSwapchainWriteCount++; }
        return info.OperationIndex;
    }

    private int RecordIndirectDrawPayload(scoped ref PrimaryCommandBufferRecordingState state, in IndirectDrawPayload payload, in VulkanPrimaryOperationRecordingInfo info)
    {
        // Producer-complete indirect packets have an immutable secondary-command
        // path. Pipeline admission intentionally does not re-warm a reusable
        // secondary, so this must be attempted before the direct fallback. The
        // fallback remains necessary when the packet cannot satisfy secondary
        // inheritance or resource-preparation invariants.
        int secondaryRunCount = CountContiguousIndirectCommandChainRun(
            ref state,
            info.OperationIndex,
            info.PassIndex);
        if (secondaryRunCount > 0 &&
            TryExecuteIndirectCommandChainSecondaryRun(
                ref state,
                info.OperationIndex,
                secondaryRunCount,
                info.PassIndex))
        {
            return checked(info.OperationIndex + secondaryRunCount - 1);
        }

        XRFrameBuffer? target = state.Ops.GetTarget(info.OperationIndex);
        EmitIndirectDrawRunReadBarrier(ref state);
        if (info.BeginsRendering) BeginRenderPassForTarget(ref state, target, info.PassIndex, state.ActiveContext);
        CmdBeginLabel(state.CommandBuffer, "IndirectDraw");
        RecordIndirectDrawPayloadIntoCommandBuffer(ref state, state.CommandBuffer, in payload, target, state.ActiveContext, info.PassIndex, info.OperationIndex);
        CmdEndLabel(state.CommandBuffer);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(false, false, 1);
        if (target is null) state.ActualSwapchainWriteCount++;
        return info.OperationIndex;
    }

    private bool TryPreparePrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int operationIndex, out int passIndex)
    {
        ref readonly FrameOpContext context = ref state.Ops.GetContext(operationIndex);
        if (!UpdatePrimaryRecordingContext(ref state, in context, state.Ops.GetTarget(operationIndex), header.PassIndex)) { passIndex = int.MinValue; return false; }
        passIndex = header.PassIndex;
        if (passIndex == int.MinValue) { RecordDroppedPrimaryOperation(ref state, header.OpCode); return false; }
        if (state.SkipUiPipelineOps && context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline) { RecordDroppedPrimaryOperation(ref state, header.OpCode); return false; }
        TransitionToPrimaryOperationPass(ref state, in node, in header, operationIndex, passIndex);
        return true;
    }

    private bool UpdatePrimaryRecordingContext(scoped ref PrimaryCommandBufferRecordingState state, in FrameOpContext context, XRFrameBuffer? target, int passIndex)
    {
        if (state.HasActiveContext && FrameOpContextCompatibility.AreRecordingCompatible(state.ActiveContext, context)) return true;
        bool preserve = state.RenderScope.ShouldPreserveForContextChange(target is null, target, passIndex, state.ActiveInlineQuery is not null, context.SchedulingIdentity, state.ActivePassIndex, state.ActiveSchedulingIdentity, FrameOpContextCompatibility.AreQueryScopeCompatible(state.ActiveContext, context));
        if (!preserve) EndActiveRenderPass(ref state);
        if (!preserve && state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        state.ActiveContext = context; state.HasActiveContext = true; ApplyPipelineOverride(ref state, state.ActiveContext);
        if (!UpdatePrimaryResourcePlannerContext(ref state)) return false;
        if (preserve) state.ActiveSchedulingIdentity = context.SchedulingIdentity; else { state.ActivePassIndex = int.MinValue; state.ActiveSchedulingIdentity = int.MinValue; }
        return true;
    }

    private bool UpdatePrimaryResourcePlannerContext(scoped ref PrimaryCommandBufferRecordingState state)
    {
        state.RenderGraphPlan = ResolvePrimaryRenderGraphPlan(ref state, in state.ActiveContext);
        if (state.ActiveResourcePlannerScopeSet)
        {
            state.ActiveResourcePlannerScope.Dispose();
            state.ActiveResourcePlannerScopeSet = false;
        }
        if (state.FramePlan is not null &&
            (state.ActiveContext.ResourceRegistry is not null || state.ActiveContext.PassMetadata is { Count: > 0 }))
        {
            if (!state.FramePlan.TryGetRecordingPlannerGeneration(in state.ActiveContext, out ResourcePlannerRuntimeGeneration generation))
            {
                throw new VulkanPlanPreconditionException(
                    "Primary recording has no frozen physical-resource generation for its operation context.");
            }
            // Attachment wrappers and prepared compute descriptors must resolve
            // the same frozen physical images, not the planner publication which
            // happened to be current before this accepted frame was prepared.
            state.ActiveResourcePlannerScope = new(ThreadWorkspace.Current, this, generation);
            state.ActiveResourcePlannerScopeSet = true;
        }
        state.PlannerContext = state.ActiveContext;
        state.HasPlannerContext = true;
        return true;
    }

    private static VulkanRenderGraphPlan ResolvePrimaryRenderGraphPlan(scoped ref PrimaryCommandBufferRecordingState state, in FrameOpContext context)
    {
        if (state.FramePlan is not null && state.FramePlan.TryResolveRenderGraphPlan(in context, out VulkanRenderGraphPlan plan)) return plan;
        if (context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 }) return state.RenderGraphPlan;
        throw new VulkanPlanPreconditionException($"Primary recording has no frozen render-graph publication for kind={context.ContextKind} pipe={context.PipelineIdentity} viewport={context.ViewportIdentity} resourceGeneration={context.ResourceGeneration}.");
    }

    private void TransitionToPrimaryOperationPass(scoped ref PrimaryCommandBufferRecordingState state, in VulkanPrimaryPlanNode node, in FrameOperationHeader header, int operationIndex, int passIndex)
    {
        int schedulingIdentity = state.Ops.GetContext(operationIndex).SchedulingIdentity;
        if (!HasPrimaryPlanAction(node.Actions, EVulkanPrimaryPlanAction.BarrierBatch) || (passIndex == state.ActivePassIndex && schedulingIdentity == state.ActiveSchedulingIdentity)) return;
        EndActiveRenderPass(ref state);
        if (state.PassIndexLabelActive) { _deviceContext.CmdEndLabel(state.CommandBuffer); state.PassIndexLabelActive = false; }
        if (_deviceContext.CanRecordCommandBufferDebugLabels) state.PassIndexLabelActive = _deviceContext.CmdBeginLabel(state.CommandBuffer, $"Pass={passIndex} Pipe={state.ActiveContext.PipelineIdentity} Vp={state.ActiveContext.ViewportIdentity}");
        EmitPassBarriers(ref state, passIndex);
        state.ActivePassIndex = passIndex; state.ActiveSchedulingIdentity = schedulingIdentity;
    }

    private void HandlePrimaryOperationRecordingFailure(scoped ref PrimaryCommandBufferRecordingState state, in FrameOperationHeader header, int operationIndex, Exception exception)
    {
        RecordDroppedPrimaryOperation(ref state, header.OpCode, true);
        EndActiveRenderPass(ref state);
        Debug.VulkanEvery($"Vulkan.FrameOpError.{GetHashCode()}", TimeSpan.FromSeconds(1), "[Vulkan] Frame op recording failed for {0}: {1}: {2}", header.OpCode, exception.GetType().Name, exception.Message);
    }

    private static void RecordDroppedPrimaryOperation(scoped ref PrimaryCommandBufferRecordingState state, EVulkanPrimaryPlanNodeKind kind, bool countIndirectCompute = false)
    {
        state.Metrics.DroppedFrameOps++;
        if (kind is EVulkanPrimaryPlanNodeKind.MeshDraw or EVulkanPrimaryPlanNodeKind.IndirectDraw or EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount) state.Metrics.DroppedDrawOps++;
        if (kind == EVulkanPrimaryPlanNodeKind.ComputeDispatch || countIndirectCompute && kind == EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect) state.Metrics.DroppedComputeOps++;
    }

    private static bool HasPrimaryPlanAction(EVulkanPrimaryPlanAction actions, EVulkanPrimaryPlanAction action) => (actions & action) != 0;
}
