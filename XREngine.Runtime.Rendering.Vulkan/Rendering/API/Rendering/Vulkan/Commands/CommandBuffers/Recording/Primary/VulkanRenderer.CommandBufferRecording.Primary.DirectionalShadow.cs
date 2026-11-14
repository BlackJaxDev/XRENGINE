using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Data.Geometry;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private static readonly string[] DirectionalCascadeGpuProfilerPath = ["Advanced", "Shadows", "DirectionalCascades"];

    /// <summary>
    /// Push block of the directional shadow lane programs: the canonical raster
    /// header followed by the cascade's row-vector world-to-clip matrix.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AdvancedDirectionalShadowRasterPushConstants(
        uint meshArgumentBase,
        uint producerAndOrigin,
        uint cascadeIndex,
        uint flags,
        in Matrix4x4 viewProjection)
    {
        public readonly uint MeshArgumentBase = meshArgumentBase;
        public readonly uint ProducerAndOrigin = producerAndOrigin;
        public readonly uint CascadeIndex = cascadeIndex;
        public readonly uint Flags = flags;
        public readonly Matrix4x4 ViewProjection = viewProjection;
    }

    /// <summary>
    /// Records one deferred directional cascade group: the family's sealed
    /// canonical bins are drawn once per cascade into the atlas page with a
    /// depth-only pipeline, each cascade clearing and rasterizing only its tile.
    /// Casters come from the per-record masks that family preparation derived
    /// from the publication's cast-shadow flags and candidate bounds, so the
    /// recorded draw set matches the generic layered pass.
    /// </summary>
    private int RecordAdvancedDirectionalShadowRasterPayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        FramePlan framePlan = state.FramePlan
            ?? throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without an accepted frame plan.");
        VulkanAdvancedDirectionalShadowLaneStorage lane = payload.Request.DirectionalShadowLane
            ?? throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without its cascade group.");
        VulkanPreparedStableBinStream bins = framePlan.GetAdvancedVisibilityFamilyBins(
            payload.Request.Reservation, allowCreate: false);
        if (!payload.State.IsValid || !payload.SceneState.IsValid ||
            !payload.TargetClosure.IsValid ||
            payload.TargetClosure.Kind != EVulkanAdvancedVisibilityTargetKind.DirectionalShadow ||
            !bins.HasSealedSubmissionPlans || lane.CascadeCount <= 0 ||
            lane.RenderFrameId != payload.Request.RenderFrameId ||
            !ReferenceEquals(lane.Target, payload.Request.Target) ||
            (!payload.DirectionalShadowResources.IsValid &&
             lane.RecordMaskCount != bins.RecordCount) || !info.BeginsRendering)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster reached recording without its sealed cascade group, target and stable-bin closure.");
        }
        if (ResourceRuntime.BackendObjects.Get(payload.Request.Target) is not
                VkFrameBuffer targetWrapper)
        {
            throw new VulkanPlanPreconditionException(
                "Advanced directional shadow raster lost its authoritative Vulkan framebuffer wrapper.");
        }
        if (state.Policy.AllowSynchronousResourceUploads)
            targetWrapper.EnsureCurrent();
        if (!targetWrapper.TryCaptureRecordedRenderTargetSnapshot(
                out VulkanRecordedRenderTargetSnapshot currentTarget) ||
            currentTarget != payload.TargetClosure.NativeTarget)
        {
            string mismatch = currentTarget.IsComplete
                ? payload.TargetClosure.NativeTarget.DescribeFirstMismatch(in currentTarget)
                : "the current native target is incomplete";
            throw new VulkanPlanPreconditionException(
                $"Advanced directional shadow atlas page changed after sealing: {mismatch}.");
        }

        if (payload.DirectionalShadowResources.IsValid)
            return RecordAdvancedDirectionalShadowGpuPayload(
                ref state, in payload, in info, bins);

        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);
        // The surrounding recorder owns the later render-scope closure.
        using var gpuScope = TryBeginVulkanGpuProfilerScope(state.CommandBuffer, DirectionalCascadeGpuProfilerPath);
        // Deformation and payload writes must be visible to the vertex stage
        // before the first cascade draw, exactly as for the visibility raster.
        EmitAdvancedVisibilityRasterReadBarrier(ref state);

        // Static cache of cascade 0: draw the static casters into the static tile when its
        // key does not match, then copy the tile into the cascade tile. The cascade loop
        // below then draws only the dynamic casters of that cascade over the copy.
        int cacheEntry = -1;
        if (lane.UsesStaticCache)
        {
            cacheEntry = lane.StaticCacheEntry;
            DirectionalCascadeStaticTile tile = lane.StaticTile!;
            EDirectionalShadowStaticCacheCause? mismatch =
                MatchDirectionalShadowStaticTile(lane, tile, payload.Request.RenderFrameId);
            if (mismatch is { } cause)
            {
                DirectionalShadowStaticCacheDiagnostics.RecordCause(cause);
                int staticRecords = RecordDirectionalShadowStaticTile(
                    ref state, in payload, in info, bins, lane, tile, cacheEntry);
                // The key moves with the recorded content. A group that the lane declines
                // never reaches this method, so its tile keeps the key of its content.
                tile.RecordStaticDraw(
                    payload.Request.RenderFrameId,
                    in lane.ViewProjections[cacheEntry],
                    in lane.TileRects[cacheEntry],
                    lane.Target!,
                    lane.StaticCasterEpoch,
                    lane.StaticRecordSetHash,
                    lane.StaticRecordCount);
                DirectionalShadowStaticCacheDiagnostics.RecordStaticRender(staticRecords);
            }
            CopyDirectionalShadowStaticTile(
                ref state, tile, lane.Target!, lane.Scissors[cacheEntry], info.PassIndex);
            DirectionalShadowStaticCacheDiagnostics.RecordCopy(overlayOnly: mismatch is null);
        }
        else if (lane.StaticCacheCause is { } blocked)
        {
            DirectionalShadowStaticCacheDiagnostics.RecordCause(blocked);
        }
        DirectionalShadowStaticCacheDiagnostics.WriteIfDue();

        // The page is loaded, never cleared as a whole: other lights' tiles stay
        // resident and each cascade clears only its own tile below.
        BeginRenderPassForTarget(
            ref state,
            payload.Request.Target,
            info.PassIndex,
            state.ActiveContext);
        if (!state.RenderScope.IsActive ||
            state.RenderScope.Target != payload.TargetClosure.Target ||
            !state.RenderScope.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly ||
            !state.RenderScope.DynamicRenderingFormats.Equals(
                payload.TargetClosure.DynamicRenderingFormats))
        {
            throw new VulkanPlanPreconditionException(
                "The active directional shadow render scope does not match its sealed atlas-page closure.");
        }

        CmdBeginLabel(state.CommandBuffer, "Advanced.DirectionalShadowRaster");
        ReadOnlySpan<Viewport> viewports = lane.Viewports;
        ReadOnlySpan<Rect2D> scissors = lane.Scissors;
        for (int cascade = 0; cascade < lane.CascadeCount; cascade++)
        {
            Viewport viewport = viewports[cascade];
            Rect2D scissor = scissors[cascade];
            SetViewportScissorTracked(state.CommandBuffer, in viewport, in scissor);
            if (cascade == cacheEntry)
            {
                // The copy of the static tile already filled this tile.
                int dynamicRecords = RecordDirectionalShadowCascadeCasters(
                    ref state, in payload, bins, lane, cascade,
                    EDirectionalShadowCasterSelection.DynamicOnly);
                DirectionalShadowStaticCacheDiagnostics.RecordDynamicDraws(dynamicRecords);
                continue;
            }

            ClearDirectionalShadowTile(ref state, lane, in scissor);
            RecordDirectionalShadowCascadeCasters(
                ref state, in payload, bins, lane, cascade,
                EDirectionalShadowCasterSelection.All);
        }
        CmdEndLabel(state.CommandBuffer);
        return info.OperationIndex;
    }
    /// <summary>Caster records that one cascade draw takes from the record masks.</summary>
    private enum EDirectionalShadowCasterSelection
    {
        /// <summary>Every caster record of the cascade.</summary>
        All,
        /// <summary>Only records without GPUIndirectRenderFlags.ShadowCasterDynamic (the static caster tile).</summary>
        StaticOnly,
        /// <summary>Only records with GPUIndirectRenderFlags.ShadowCasterDynamic (the overlay over a copied static tile).</summary>
        DynamicOnly,
    }

    /// <summary>
    /// Compares the key of the static caster tile with cascade 0 of this group. Returns null
    /// when the tile holds exactly the static casters that this frame would draw: the same
    /// matrix (all 16 values), tile rectangle and page, static caster epoch and static record
    /// set, from an earlier render frame. Otherwise returns the first reason that fails.
    /// </summary>
    private static EDirectionalShadowStaticCacheCause? MatchDirectionalShadowStaticTile(
        VulkanAdvancedDirectionalShadowLaneStorage lane,
        DirectionalCascadeStaticTile tile,
        ulong renderFrameId)
    {
        if (!tile.HasContent)
            return EDirectionalShadowStaticCacheCause.NoStaticContent;
        // A retry of the same render frame records the static draw again: its first
        // recording may not have reached the GPU.
        if (tile.RecordedRenderFrameId >= renderFrameId)
            return EDirectionalShadowStaticCacheCause.SameFrame;

        int entry = lane.StaticCacheEntry;
        if (tile.RecordedViewProjection != lane.ViewProjections[entry])
            return EDirectionalShadowStaticCacheCause.FitMismatch;

        BoundingRectangle rect = lane.TileRects[entry];
        BoundingRectangle recordedRect = tile.RecordedTileRect;
        if (!ReferenceEquals(tile.RecordedPage, lane.Target) ||
            recordedRect.X != rect.X || recordedRect.Y != rect.Y ||
            recordedRect.Width != rect.Width || recordedRect.Height != rect.Height)
        {
            return EDirectionalShadowStaticCacheCause.TileMismatch;
        }

        if (tile.RecordedStaticEpoch != lane.StaticCasterEpoch)
            return EDirectionalShadowStaticCacheCause.StaticEpochChanged;
        if (tile.RecordedRecordSetHash != lane.StaticRecordSetHash ||
            tile.RecordedRecordCount != lane.StaticRecordCount)
        {
            return EDirectionalShadowStaticCacheCause.RecordSetChanged;
        }

        return null;
    }

    /// <summary>
    /// Draws the static casters of cascade 0 into the static tile: begins rendering into the
    /// tile framebuffer, clears it, draws the static records with the cascade 0 matrix, and
    /// ends rendering. The tile has the size of the cascade's inner tile rectangle, so each
    /// texel gets the same depth as the same texel of a full cascade draw. Returns the number
    /// of static records drawn.
    /// </summary>
    private int RecordDirectionalShadowStaticTile(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info,
        VulkanPreparedStableBinStream bins,
        VulkanAdvancedDirectionalShadowLaneStorage lane,
        DirectionalCascadeStaticTile tile,
        int cacheEntry)
    {
        BeginRenderPassForTarget(ref state, tile.FrameBuffer, info.PassIndex, state.ActiveContext);
        if (!state.RenderScope.IsActive ||
            !state.RenderScope.UsesDynamicRendering ||
            state.RenderScope.DepthStencilReadOnly ||
            !state.RenderScope.DynamicRenderingFormats.Equals(
                payload.TargetClosure.DynamicRenderingFormats))
        {
            throw new VulkanPlanPreconditionException(
                "The static directional cascade tile does not have the depth format of its atlas page.");
        }

        CmdBeginLabel(state.CommandBuffer, "Advanced.DirectionalShadowStaticTile");
        Extent2D extent = new(tile.Width, tile.Height);
        BoundingRectangle rect = new(0, 0, (int)tile.Width, (int)tile.Height);
        Viewport viewport = CreateVulkanViewport(rect, extent);
        Rect2D scissor = VulkanStateTracker.CreateVulkanScissor(rect, extent);
        SetViewportScissorTracked(state.CommandBuffer, in viewport, in scissor);
        ClearDirectionalShadowTile(ref state, lane, in scissor);
        int drawn = RecordDirectionalShadowCascadeCasters(
            ref state, in payload, bins, lane, cacheEntry,
            EDirectionalShadowCasterSelection.StaticOnly);
        CmdEndLabel(state.CommandBuffer);
        EndActiveRenderPass(ref state);
        return drawn;
    }

    /// <summary>
    /// Copies the static tile into the inner rectangle of cascade 0 on the atlas page
    /// (<paramref name="pageScissor"/>). Both images move to transfer layouts with barriers
    /// that come from their recorded access state, and return to the depth attachment layout.
    /// The copy covers the inner rectangle: the cascade draw writes only that rectangle (its
    /// scissor), and receivers clamp their filter taps to its texel centres, so the gutter
    /// around it is never written and never sampled.
    /// </summary>
    private void CopyDirectionalShadowStaticTile(
        scoped ref PrimaryCommandBufferRecordingState state,
        DirectionalCascadeStaticTile tile,
        XRFrameBuffer page,
        in Rect2D pageScissor,
        int passIndex)
    {
        if (state.RenderScope.IsActive)
            EndActiveRenderPass(ref state);

        BlitPayload operation = new(
            tile.FrameBuffer, page,
            0, 0, tile.Width, tile.Height,
            pageScissor.Offset.X, pageScissor.Offset.Y, tile.Width, tile.Height,
            EReadBufferMode.None, ColorBit: false, DepthBit: true, StencilBit: false,
            LinearFilter: false, RequireExactCompatibility: true);
        if (!TryResolvePreparedBlitImage(tile.FrameBuffer, EReadBufferMode.None,
                wantColor: false, wantDepth: true, wantStencil: false,
                out BlitImageInfo source, isSource: true, passIndex, in operation, in state.SwapchainTarget) ||
            !TryResolvePreparedBlitImage(page, EReadBufferMode.None,
                wantColor: false, wantDepth: true, wantStencil: false,
                out BlitImageInfo destination, isSource: false, passIndex, in operation, in state.SwapchainTarget))
        {
            throw new VulkanPlanPreconditionException(
                "The static directional cascade tile or its atlas page has no depth image to copy.");
        }
        source = RequirePreparedBlitImage(source, "source");
        destination = RequirePreparedBlitImage(destination, "destination");
        if (source.Format != destination.Format ||
            pageScissor.Extent.Width != tile.Width || pageScissor.Extent.Height != tile.Height)
        {
            throw new VulkanPlanPreconditionException(
                "The static directional cascade tile does not match the format or size of its cascade tile.");
        }

        const PipelineStageFlags DepthStages =
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        const AccessFlags DepthAccess =
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
        TransitionPreparedImageForBlit(
            state.CommandBuffer, source, source.PreferredLayout, ImageLayout.TransferSrcOptimal,
            source.AccessMask, AccessFlags.TransferReadBit, source.StageMask, PipelineStageFlags.TransferBit);
        TransitionPreparedImageForBlit(
            state.CommandBuffer, destination, destination.PreferredLayout, ImageLayout.TransferDstOptimal,
            destination.AccessMask, AccessFlags.TransferWriteBit, destination.StageMask, PipelineStageFlags.TransferBit);

        ImageCopy region = new()
        {
            SrcSubresource = new ImageSubresourceLayers(source.AspectMask, source.MipLevel, source.BaseArrayLayer, 1u),
            SrcOffset = new Offset3D(0, 0, 0),
            DstSubresource = new ImageSubresourceLayers(destination.AspectMask, destination.MipLevel, destination.BaseArrayLayer, 1u),
            DstOffset = new Offset3D(pageScissor.Offset.X, pageScissor.Offset.Y, 0),
            Extent = new Extent3D(tile.Width, tile.Height, 1u),
        };
        PrimaryCommandEncoder.CopyImage(state.CommandBuffer, source.Image, destination.Image, ref region);

        TransitionPreparedImageForBlit(
            state.CommandBuffer, source, ImageLayout.TransferSrcOptimal, ImageLayout.DepthStencilAttachmentOptimal,
            AccessFlags.TransferReadBit, DepthAccess, PipelineStageFlags.TransferBit, DepthStages);
        TransitionPreparedImageForBlit(
            state.CommandBuffer, destination, ImageLayout.TransferDstOptimal, ImageLayout.DepthStencilAttachmentOptimal,
            AccessFlags.TransferWriteBit, DepthAccess, PipelineStageFlags.TransferBit, DepthStages);
    }

    /// <summary>Clears one tile of the active depth target to the lane's depth clear value.</summary>
    private unsafe void ClearDirectionalShadowTile(
        scoped ref PrimaryCommandBufferRecordingState state,
        VulkanAdvancedDirectionalShadowLaneStorage lane,
        in Rect2D scissor)
    {
        ClearAttachment tileClear = new()
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ClearValue = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue
                {
                    Depth = lane.DepthClearValue,
                    Stencil = 0u,
                },
            },
        };
        ClearRect tileRect = new()
        {
            Rect = scissor,
            BaseArrayLayer = 0u,
            LayerCount = 1u,
        };
        Api!.CmdClearAttachments(state.CommandBuffer, 1u, &tileClear, 1u, &tileRect);
    }

    /// <summary>
    /// Draws the caster records of one cascade entry with the frozen indexed arguments and
    /// the entry's matrix into the active render scope (viewport and scissor already set).
    /// <paramref name="selection"/> picks all records, only static records, or only dynamic
    /// records (GPUIndirectRenderFlags.ShadowCasterDynamic). Returns the number of records drawn.
    /// </summary>
    private int RecordDirectionalShadowCascadeCasters(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        VulkanPreparedStableBinStream bins,
        VulkanAdvancedDirectionalShadowLaneStorage lane,
        int cascade,
        EDirectionalShadowCasterSelection selection)
    {
        ReadOnlySpan<VulkanPreparedStableBinHeader> headers = bins.Headers;
        ReadOnlySpan<VulkanPreparedStableBinRecord> allRecords = bins.Records;
        ReadOnlySpan<byte> masks = lane.RecordMasks;
        ReadOnlySpan<Matrix4x4> viewProjections = lane.ViewProjections;
        ShaderStageFlags pushStages =
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext);
        byte cascadeBit = (byte)(1 << cascade);
        int drawn = 0;
        for (int headerIndex = 0; headerIndex < headers.Length; headerIndex++)
        {
            ref readonly VulkanPreparedStableBinHeader header = ref headers[headerIndex];
            // Mesh surface decals never cast shadows.
            if (header.IsSurfaceDecal)
                continue;
            if (!header.IsRasterReady)
            {
                throw new VulkanPlanPreconditionException(
                    "A non-empty visibility bin has no sealed raster pipeline and submission plan.");
            }

            VulkanVisibilityRasterPipeline shadow = header.ShadowRasterPipeline;
            if (!shadow.IsValid || shadow.TargetClosure != payload.TargetClosure ||
                shadow.Program.LinkGeneration != shadow.ProgramLinkGeneration)
            {
                throw new VulkanPlanPreconditionException(
                    "A directional shadow pipeline closure changed after frame-plan sealing.");
            }
            VulkanSealedBinSubmissionPlan plan = header.SubmissionPlan!;
            EAdvancedGeometryProducer binProducer = header.IndirectRange.Key.Producer;
            // This branch draws frozen indexed arguments with CPU cascade masks:
            // CPU-direct bins, and the static meshlet and indexed bins of a
            // meshlet frame (shadow preparation admitted only those).
            bool frozenIndexedBin =
                plan.ResolvedStrategy == EMeshSubmissionStrategy.CpuDirect ||
                (plan.ResolvedStrategy == EMeshSubmissionStrategy.GpuMeshletZeroReadback &&
                 binProducer is EAdvancedGeometryProducer.StaticMeshlet or EAdvancedGeometryProducer.SkinnedMeshlet) ||
                (plan.ResolvedStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback &&
                 binProducer == EAdvancedGeometryProducer.IndirectIndexed);
            if (!frozenIndexedBin)
            {
                throw new VulkanPlanPreconditionException(
                    "The directional shadow lane reached CPU-masked recording with a bin that has no frozen indexed arguments.");
            }
            // The shadow vertex stage reads the payload row from the base instance
            // for every producer except IndirectIndexed, which reads it from the
            // GPU-written mesh payload table. A frozen indexed draw sets the base
            // instance to the payload row, so an indexed range of a meshlet frame
            // is pushed as a CPU-direct static range.
            uint pushedProducer = binProducer == EAdvancedGeometryProducer.IndirectIndexed
                ? (uint)EAdvancedGeometryProducer.CpuDirectStaticIndexed
                : (uint)binProducer;

            // Skip the bind when no record of this bin reaches the cascade.
            int firstRecord = -1;
            int recordEnd = header.RecordOffset + header.RecordCount;
            for (int recordIndex = header.RecordOffset; recordIndex < recordEnd; recordIndex++)
            {
                if (IsSelectedDirectionalShadowCaster(masks[recordIndex], cascadeBit, allRecords[recordIndex], selection))
                {
                    firstRecord = recordIndex;
                    break;
                }
            }
            if (firstRecord < 0)
                continue;

            VulkanResidentDrawTemplateNativeState native = header.NativeState;
            if (native.PrimitiveCount != 1 ||
                native.Primitive0.Topology != PrimitiveTopology.TriangleList ||
                !native.Primitive0.Indexed || native.VertexBufferCount != 1 ||
                native.GetVertexBinding(0) != 0u ||
                native.Primitive0.IndexBuffer.Handle == 0)
            {
                throw new VulkanPlanPreconditionException(
                    "A directional shadow bin has an invalid canonical packed-geometry closure.");
            }

            using (VulkanCpuStageScope bindingStage = new(
                       _frameTelemetry,
                       EVulkanCpuStage.PrimaryAdvancedRasterBindingOperation))
            {
                BindPipelineTracked(
                    state.CommandBuffer,
                    PipelineBindPoint.Graphics,
                    shadow.Pipeline);
                BindAdvancedVisibilityDescriptorSets(
                    state.CommandBuffer,
                    PipelineBindPoint.Graphics,
                    shadow.PipelineLayout,
                    in payload);
                VulkanVisibilityGeometryRecordClosure geometryClosure =
                    allRecords[header.RecordOffset].VisibilityGeometryClosure;
                BindVertexBufferTracked(
                    state.CommandBuffer,
                    native.GetVertexBinding(0),
                    native.GetVertexBuffer(0),
                    geometryClosure.ResolveVertexSource(
                        bins.VisibilityGeometrySources.CurrentVertices).Offset);
                BindIndexBufferTracked(
                    state.CommandBuffer,
                    native.Primitive0.IndexBuffer,
                    geometryClosure.IndexSlice.Offset,
                    native.Primitive0.IndexType);
                PushConstantsTracked(
                    state.CommandBuffer,
                    shadow.PipelineLayout,
                    pushStages,
                    0u,
                    new AdvancedDirectionalShadowRasterPushConstants(
                        header.IndirectRange.FirstPayloadIndex,
                        pushedProducer,
                        (uint)cascade,
                        1u,
                        in viewProjections[cascade]));
            }

            using VulkanCpuStageScope drawStage = new(
                _frameTelemetry,
                EVulkanCpuStage.PrimaryAdvancedCpuDirectDrawOperation);
            for (int recordIndex = firstRecord; recordIndex < recordEnd; recordIndex++)
            {
                if (!IsSelectedDirectionalShadowCaster(masks[recordIndex], cascadeBit, allRecords[recordIndex], selection))
                    continue;

                VulkanPreparedVisibilityDirectDraw draw =
                    allRecords[recordIndex].VisibilityDirectDraw;
                if (!draw.IsValid)
                {
                    throw new VulkanPlanPreconditionException(
                        "A directional shadow bin contains invalid frozen indexed arguments.");
                }
                Api!.CmdDrawIndexed(
                    state.CommandBuffer,
                    draw.IndexCount,
                    draw.InstanceCount,
                    draw.FirstIndex,
                    draw.VertexOffset,
                    draw.FirstInstance);
                drawn++;
            }
        }
        return drawn;
    }

    private static bool IsSelectedDirectionalShadowCaster(
        byte mask,
        byte cascadeBit,
        in VulkanPreparedStableBinRecord record,
        EDirectionalShadowCasterSelection selection)
    {
        if ((mask & cascadeBit) == 0)
            return false;
        if (selection == EDirectionalShadowCasterSelection.All)
            return true;
        bool dynamicCaster =
            (record.VisibilityDrawFlags & (uint)GPUIndirectRenderFlags.ShadowCasterDynamic) != 0u;
        return dynamicCaster == (selection == EDirectionalShadowCasterSelection.DynamicOnly);
    }
}
