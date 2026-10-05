using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal void BeginTemporalHistoryRecording(in VulkanTemporalHistoryNativeCoverage sealedCoverage)
    {
        VulkanCommandThreadContext context = ThreadWorkspace.Current;
        if (context.TemporalHistoryCoverage.Active)
            throw new VulkanPlanPreconditionException("A temporal history primary is already recording on this thread.");
        context.TemporalHistoryCoverage = sealedCoverage;
    }

    internal VulkanTemporalHistoryNativeCoverage EndTemporalHistoryRecording()
    {
        VulkanCommandThreadContext context = ThreadWorkspace.Current;
        VulkanTemporalHistoryNativeCoverage coverage = context.TemporalHistoryCoverage;
        context.TemporalHistoryCoverage = default;
        return coverage;
    }

    private void RecordTemporalHistoryBlit(
        in BlitPayload operation,
        in BlitImageInfo source,
        in BlitImageInfo destination,
        in ImageBlit region)
    {
        if (!ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) ||
            !context.TemporalHistoryCoverage.Active)
            return;

        ref VulkanTemporalHistoryNativeCoverage coverage = ref context.TemporalHistoryCoverage;
        TemporalHistorySubmissionCandidate candidate = coverage.Candidate;
        if (ReferenceEquals(operation.OutFbo, candidate.ColorHistoryTarget) &&
            (!coverage.TemporalResolveColor.IsValid || coverage.TemporalResolveRecorded) &&
            MatchesTemporalBlitImage(in coverage.ColorCopySource, in source, in region))
            coverage.ColorLayerMask |= GetFullyWrittenTemporalLayers(
                candidate.WriteResources.Color, in destination, in region);
        if (ReferenceEquals(operation.OutFbo, candidate.DepthHistoryTarget))
            coverage.DepthLayerMask |= GetFullyWrittenTemporalLayers(
                candidate.WriteResources.Depth, in destination, in region);
        if (ReferenceEquals(operation.OutFbo, candidate.TsrColorHistoryTarget) &&
            coverage.TsrResolveRecorded &&
            MatchesTemporalBlitImage(in coverage.TsrColorCopySource, in source, in region))
            coverage.TsrColorLayerMask |= GetFullyWrittenTemporalLayers(
                candidate.WriteResources.TsrColor, in destination, in region);
        if (ReferenceEquals(operation.OutFbo, candidate.MetadataHistoryTarget) &&
            coverage.TsrResolveRecorded &&
            MatchesTemporalBlitImage(in coverage.MetadataCopySource, in source, in region))
            coverage.MetadataLayerMask |= GetFullyWrittenTemporalLayers(
                candidate.WriteResources.Metadata, in destination, in region);
        if (ReferenceEquals(operation.OutFbo, candidate.ExposureHistoryTarget) &&
            coverage.TemporalResolveRecorded &&
            MatchesTemporalBlitImage(in coverage.ExposureCopySource, in source, in region))
            coverage.ExposureLayerMask |= GetFullyWrittenTemporalLayers(
                candidate.WriteResources.Exposure, in destination, in region);
    }

    private bool MatchesTemporalBlitImage(
        in TemporalHistoryResourceIdentity expected,
        in BlitImageInfo image,
        in ImageBlit region)
    {
        if (!expected.IsValid || image.Image.Handle == 0 ||
            region.SrcOffsets.Element0.X != 0 || region.SrcOffsets.Element0.Y != 0 ||
            region.SrcOffsets.Element1.X != image.Extent.Width ||
            region.SrcOffsets.Element1.Y != image.Extent.Height ||
            region.SrcSubresource.AspectMask != image.AspectMask ||
            region.SrcSubresource.MipLevel != image.MipLevel ||
            region.SrcSubresource.BaseArrayLayer != image.BaseArrayLayer ||
            region.SrcSubresource.LayerCount != image.LayerCount)
            return false;
        TemporalHistoryResourceIdentity actual = new(
            ResourceRuntime, image.Image.Handle,
            ResourceRuntime.GetPublishedGeneration(ObjectType.Image, image.Image.Handle),
            (uint)image.AspectMask, image.MipLevel, 1,
            image.BaseArrayLayer, image.LayerCount);
        return expected.Matches(in actual);
    }

    private bool IsTemporalResolveProducer(int originalIndex)
    {
        if (!ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) ||
            !context.TemporalHistoryCoverage.Active)
            return false;
        ref VulkanTemporalHistoryNativeCoverage coverage = ref context.TemporalHistoryCoverage;
        return originalIndex == coverage.TemporalResolveOriginalIndex ||
               originalIndex == coverage.TsrResolveOriginalIndex;
    }

    private void RecordTemporalResolveProducer(
        scoped ref PrimaryCommandBufferRecordingState state,
        int originalIndex,
        in PendingMeshDraw draw,
        bool recorded)
    {
        if (!recorded ||
            !ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context) ||
            !context.TemporalHistoryCoverage.Active)
            return;

        ref VulkanTemporalHistoryNativeCoverage coverage = ref context.TemporalHistoryCoverage;
        bool temporal = originalIndex == coverage.TemporalResolveOriginalIndex;
        bool tsr = originalIndex == coverage.TsrResolveOriginalIndex;
        if (!temporal && !tsr)
            return;
        XRFrameBuffer? target = temporal
            ? coverage.Candidate.TemporalResolveTarget
            : coverage.Candidate.TsrResolveTarget;
        TemporalHistoryResourceIdentity color = temporal
            ? coverage.TemporalResolveColor
            : coverage.TsrResolveColor;
        TemporalHistoryResourceIdentity auxiliary = temporal
            ? coverage.TemporalResolveAuxiliary
            : coverage.TsrResolveAuxiliary;
        uint width = temporal ? coverage.TemporalResolveWidth : coverage.TsrResolveWidth;
        uint height = temporal ? coverage.TemporalResolveHeight : coverage.TsrResolveHeight;
        const ColorComponentFlags requiredColorChannels =
            ColorComponentFlags.RBit | ColorComponentFlags.GBit |
            ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        uint colorIndex = temporal
            ? coverage.TemporalResolveColorIndex
            : coverage.TsrResolveColorIndex;
        uint auxiliaryIndex = temporal
            ? coverage.TemporalResolveAuxiliaryIndex
            : coverage.TsrResolveAuxiliaryIndex;
        if (!ReferenceEquals(state.RenderScope.Target, target) ||
            !state.RenderScope.IsActive || !state.RenderScope.UsesDynamicRendering ||
            (draw.ColorWriteMask & requiredColorChannels) != requiredColorChannels ||
            (state.RenderScope.DynamicRenderingFormats.ViewMask &
             coverage.Candidate.ExpectedLayerMask) != coverage.Candidate.ExpectedLayerMask ||
            coverage.Candidate.ExpectedLayerMask != 0b11u ||
            !CoversTemporalResolveExtent(state.RenderScope.RenderArea, in draw, width, height) ||
            ResourceRuntime.BackendObjects.Get(target!) is not VkFrameBuffer frameBuffer ||
            !MatchesBoundTemporalAttachment(frameBuffer,
                state.RenderScope.AttachmentSignature, colorIndex, in color) ||
            (auxiliary.IsValid && !MatchesBoundTemporalAttachment(frameBuffer,
                state.RenderScope.AttachmentSignature, auxiliaryIndex, in auxiliary)))
            return;

        if (temporal)
            coverage.TemporalResolveRecorded = true;
        else
            coverage.TsrResolveRecorded = true;
    }

    private static bool CoversTemporalResolveExtent(
        in Rect2D renderArea,
        in PendingMeshDraw draw,
        uint width,
        uint height)
    {
        if (width == 0 || height == 0 || draw.ViewportScissorCount != 1 ||
            renderArea.Offset.X != 0 || renderArea.Offset.Y != 0 ||
            renderArea.Extent.Width < width || renderArea.Extent.Height < height ||
            draw.Scissor.Offset.X != 0 || draw.Scissor.Offset.Y != 0 ||
            draw.Scissor.Extent.Width < width || draw.Scissor.Extent.Height < height)
            return false;
        Viewport viewport = draw.Viewport;
        return MathF.Min(viewport.X, viewport.X + viewport.Width) <= 0f &&
               MathF.Max(viewport.X, viewport.X + viewport.Width) >= width &&
               MathF.Min(viewport.Y, viewport.Y + viewport.Height) <= 0f &&
               MathF.Max(viewport.Y, viewport.Y + viewport.Height) >= height;
    }

    private bool MatchesBoundTemporalAttachment(
        VkFrameBuffer frameBuffer,
        FrameBufferAttachmentSignature[]? signatures,
        uint colorIndex,
        in TemporalHistoryResourceIdentity expected)
    {
        if (!expected.IsValid || signatures is null)
            return false;
        for (int index = 0; index < signatures.Length; index++)
        {
            FrameBufferAttachmentSignature signature = signatures[index];
            if (signature.Role != AttachmentRole.Color ||
                signature.ColorIndex != colorIndex ||
                signature.StoreOp != AttachmentStoreOp.Store ||
                !frameBuffer.TryGetAttachmentView(index, out ImageView view) ||
                !ResourceRuntime.Images.TryGetDescriptorHeapCreateInfo(
                    view, out ImageViewCreateInfo viewInfo))
                continue;

            ImageSubresourceRange range = viewInfo.SubresourceRange;
            TemporalHistoryResourceIdentity actual = new(
                ResourceRuntime, viewInfo.Image.Handle,
                ResourceRuntime.GetPublishedGeneration(
                    ObjectType.Image, viewInfo.Image.Handle),
                (uint)range.AspectMask, range.BaseMipLevel, range.LevelCount,
                range.BaseArrayLayer, range.LayerCount);
            return expected.Matches(in actual);
        }
        return false;
    }

    private uint GetFullyWrittenTemporalLayers(
        in TemporalHistoryResourceIdentity expected,
        in BlitImageInfo destination,
        in ImageBlit region)
    {
        if (!expected.IsValid ||
            !ReferenceEquals(expected.BackendOwner, ResourceRuntime) ||
            expected.ImageHandle != destination.Image.Handle ||
            expected.ImageGeneration != ResourceRuntime.GetPublishedGeneration(
                ObjectType.Image, destination.Image.Handle) ||
            (expected.AspectMask & (uint)region.DstSubresource.AspectMask) == 0 ||
            region.DstSubresource.MipLevel < expected.BaseMipLevel ||
            (ulong)region.DstSubresource.MipLevel >= (ulong)expected.BaseMipLevel + expected.LevelCount ||
            region.DstOffsets.Element0.X != 0 || region.DstOffsets.Element0.Y != 0 ||
            region.DstOffsets.Element1.X != destination.Extent.Width ||
            region.DstOffsets.Element1.Y != destination.Extent.Height)
            return 0;

        uint first = region.DstSubresource.BaseArrayLayer;
        uint count = region.DstSubresource.LayerCount;
        if (first < expected.BaseArrayLayer ||
            (ulong)first + count > (ulong)expected.BaseArrayLayer + expected.LayerCount)
            return 0;

        uint mask = 0;
        for (uint layer = first; layer < first + count && layer < 32; layer++)
            mask |= 1u << (int)layer;
        return mask;
    }

    /// <summary>
    /// Resolves history images from the sealed draw and blit payloads. The
    /// recording planner scope selects the same physical backing as encoding.
    /// </summary>
    internal bool TryResolveTemporalHistoryResources(
        FramePlan plan,
        FrameOperationStream operations,
        in TemporalHistorySubmissionCandidate candidate,
        out TemporalHistorySubmissionCandidate rebasedCandidate,
        out TemporalHistoryResourceSet reads,
        out TemporalHistoryResourceSet writes,
        out VulkanTemporalHistoryNativeCoverage nativeCoverage)
    {
        rebasedCandidate = default;
        reads = default;
        writes = default;
        nativeCoverage = default;
        if (!candidate.IsValid)
        {
            rebasedCandidate = candidate;
            return true;
        }

        if (!TryResolveTemporalHistoryRead(operations, plan, in candidate, candidate.ColorReadTexture, null, out var colorRead) ||
            !TryResolveTemporalHistoryRead(operations, plan, in candidate, candidate.DepthReadTexture, ImageAspectFlags.DepthBit, out var depthRead) ||
            !TryResolveTemporalHistoryRead(operations, plan, in candidate, candidate.TsrColorReadTexture, null, out var tsrRead) ||
            !TryResolveTemporalHistoryRead(operations, plan, in candidate, candidate.MetadataReadTexture, null, out var metadataRead) ||
            !TryResolveTemporalHistoryRead(operations, plan, in candidate, candidate.ExposureReadTexture, null, out var exposureRead) ||
            !TryResolveTemporalHistoryWrite(operations, plan, in candidate, candidate.ColorHistoryTarget, depth: false, out var colorWrite, out var colorTarget, out var colorSource) ||
            !TryResolveTemporalHistoryWrite(operations, plan, in candidate, candidate.DepthHistoryTarget, depth: true, out var depthWrite, out var depthTarget, out _) ||
            !TryResolveTemporalHistoryWrite(operations, plan, in candidate, candidate.TsrColorHistoryTarget, depth: false, out var tsrWrite, out var tsrTarget, out var tsrSource) ||
            !TryResolveTemporalHistoryWrite(operations, plan, in candidate, candidate.MetadataHistoryTarget, depth: false, out var metadataWrite, out var metadataTarget, out var metadataSource) ||
            !TryResolveTemporalHistoryWrite(operations, plan, in candidate, candidate.ExposureHistoryTarget, depth: false, out var exposureWrite, out var exposureTarget, out var exposureSource) ||
            !TryResolveTemporalResolveProducer(operations, plan, in candidate, candidate.TemporalResolveTarget,
                in colorSource, in exposureSource, out var temporalTarget, out var temporalIndex,
                out var temporalColor, out var temporalAux, out var temporalColorIndex,
                out var temporalAuxIndex,
                out var temporalExtent) ||
            !TryResolveTemporalResolveProducer(operations, plan, in candidate, candidate.TsrResolveTarget,
                in tsrSource, in metadataSource, out var tsrResolveTarget, out var tsrIndex,
                out var tsrColor, out var tsrAux, out var tsrColorIndex,
                out var tsrAuxIndex,
                out var tsrExtent))
            return false;

        reads = new(colorRead, depthRead, tsrRead, metadataRead, exposureRead);
        writes = new(colorWrite, depthWrite, tsrWrite, metadataWrite, exposureWrite);
        rebasedCandidate = candidate with
        {
            ColorHistoryTarget = colorTarget,
            DepthHistoryTarget = depthTarget,
            TsrColorHistoryTarget = tsrTarget,
            MetadataHistoryTarget = metadataTarget,
            ExposureHistoryTarget = exposureTarget,
            TemporalResolveTarget = temporalTarget,
            TsrResolveTarget = tsrResolveTarget,
        };
        nativeCoverage = new()
        {
            Active = true,
            Candidate = rebasedCandidate,
            TemporalResolveOriginalIndex = temporalIndex,
            TsrResolveOriginalIndex = tsrIndex,
            TemporalResolveColor = temporalColor,
            TemporalResolveAuxiliary = temporalAux,
            TsrResolveColor = tsrColor,
            TsrResolveAuxiliary = tsrAux,
            TemporalResolveColorIndex = temporalColorIndex,
            TsrResolveColorIndex = tsrColorIndex,
            TemporalResolveAuxiliaryIndex = temporalAuxIndex,
            TsrResolveAuxiliaryIndex = tsrAuxIndex,
            ColorCopySource = colorSource,
            TsrColorCopySource = tsrSource,
            MetadataCopySource = metadataSource,
            ExposureCopySource = exposureSource,
            TemporalResolveWidth = temporalExtent.Width,
            TemporalResolveHeight = temporalExtent.Height,
            TsrResolveWidth = tsrExtent.Width,
            TsrResolveHeight = tsrExtent.Height,
        };
        return true;
    }

    private bool TryResolveTemporalHistoryRead(
        FrameOperationStream operations,
        FramePlan plan,
        in TemporalHistorySubmissionCandidate candidate,
        XRTexture? texture,
        ImageAspectFlags? requestedAspect,
        out TemporalHistoryResourceIdentity identity)
    {
        identity = default;
        if (texture is null)
            return true;

        bool found = false;
        for (int index = 0; index < operations.Count; index++)
        {
            ref readonly FrameOpContext context = ref operations.GetContext(index);
            if (!ReferenceEquals(context.PipelineInstance, candidate.PipelineInstance))
                continue;
            ComputeDispatchSnapshot? snapshot = operations.TryGetMeshDraw(index, out MeshDrawPayload mesh)
                ? mesh.Draw.ProgramBindingSnapshot
                : operations.TryGetIndirectDraw(index, out IndirectDrawPayload indirect)
                    ? indirect.Draw.ProgramBindingSnapshot
                    : null;
            if (snapshot is null || context.ResourceRegistry is null ||
                string.IsNullOrEmpty(texture.Name) ||
                !context.ResourceRegistry.TryGetTexture(texture.Name, out XRTexture? canonical) ||
                canonical is null)
                continue;

            using VulkanPreparedResourcePlannerThreadScope scope =
                EnterRecordingResourceScope(plan, in context);
            if (!TryResolveSampledTemporalIdentity(
                    canonical, requestedAspect, out TemporalHistoryResourceIdentity canonicalIdentity))
                return false;

            foreach (KeyValuePair<uint, XRTexture> binding in snapshot.Samplers)
            {
                XRTexture sampledTexture = binding.Value;
                if (!IsTemporalHistorySamplerForRole(sampledTexture, canonical))
                    continue;
                if (!TryResolveSampledTemporalIdentity(sampledTexture, requestedAspect, out TemporalHistoryResourceIdentity sampledIdentity) ||
                    !canonicalIdentity.Contains(in sampledIdentity) ||
                    (found && !identity.Matches(in canonicalIdentity)))
                    return false;
                identity = canonicalIdentity;
                found = true;
            }

            foreach (KeyValuePair<string, XRTexture> binding in snapshot.SamplersByName)
            {
                XRTexture sampledTexture = binding.Value;
                if (!IsTemporalHistorySamplerForRole(sampledTexture, canonical))
                    continue;
                if (!TryResolveSampledTemporalIdentity(sampledTexture, requestedAspect, out TemporalHistoryResourceIdentity sampledIdentity) ||
                    !canonicalIdentity.Contains(in sampledIdentity) ||
                    (found && !identity.Matches(in canonicalIdentity)))
                    return false;
                identity = canonicalIdentity;
                found = true;
            }
        }

        return found;
    }

    private static bool IsTemporalHistorySamplerForRole(XRTexture sampled, XRTexture canonical)
    {
        if (ReferenceEquals(sampled, canonical))
            return true;

        XRTexture sampledBase = sampled is XRTextureViewBase sampledView
            ? sampledView.GetViewedTexture() ?? sampled
            : sampled;
        XRTexture canonicalBase = canonical is XRTextureViewBase canonicalView
            ? canonicalView.GetViewedTexture() ?? canonical
            : canonical;
        return ReferenceEquals(sampledBase, canonicalBase);
    }

    private bool TryResolveSampledTemporalIdentity(
        XRTexture texture,
        ImageAspectFlags? requestedAspect,
        out TemporalHistoryResourceIdentity identity)
    {
        identity = default;
        if (ResourceRuntime.BackendObjects.Get(texture) is not IVkImageDescriptorSource source)
            return false;

        lock (source.DescriptorSnapshotSyncRoot)
        {
            if (!source.TryGetDescriptorSnapshot(
                    null, requestedAspect, "temporal history seal", false,
                    out VkImageDescriptorSnapshot snapshot) ||
                !snapshot.IsReady || snapshot.Image.Handle == 0 || snapshot.View.Handle == 0 ||
                !ResourceRuntime.Images.TryGetDescriptorHeapCreateInfo(
                    snapshot.View, out ImageViewCreateInfo viewInfo) ||
                viewInfo.Image.Handle != snapshot.Image.Handle)
                return false;

            ulong generation = ResourceRuntime.GetPublishedGeneration(
                ObjectType.Image, snapshot.Image.Handle);
            if (generation == 0)
                return false;

            ImageSubresourceRange range = viewInfo.SubresourceRange;
            identity = new(
                ResourceRuntime,
                snapshot.Image.Handle,
                generation,
                (uint)range.AspectMask,
                range.BaseMipLevel,
                range.LevelCount,
                range.BaseArrayLayer,
                range.LayerCount);
            return identity.IsValid;
        }
    }

    private bool TryResolveTemporalHistoryWrite(
        FrameOperationStream operations,
        FramePlan plan,
        in TemporalHistorySubmissionCandidate candidate,
        XRFrameBuffer? target,
        bool depth,
        out TemporalHistoryResourceIdentity identity,
        out XRFrameBuffer? recordedTarget,
        out TemporalHistoryResourceIdentity sourceIdentity)
    {
        identity = default;
        recordedTarget = null;
        sourceIdentity = default;
        if (target is null)
            return true;

        bool found = false;
        for (int index = 0; index < operations.Count; index++)
        {
            if (operations.GetHeader(index).OpCode != EVulkanPrimaryPlanNodeKind.Blit)
                continue;
            ref readonly FrameOpContext context = ref operations.GetContext(index);
            if (!ReferenceEquals(context.PipelineInstance, candidate.PipelineInstance) ||
                context.ResourceRegistry is null || string.IsNullOrEmpty(target.Name) ||
                !context.ResourceRegistry.TryGetFrameBuffer(target.Name, out XRFrameBuffer? canonicalTarget) ||
                canonicalTarget is null)
                continue;
            ref readonly BlitPayload blit = ref operations.GetBlit(index);
            if (!ReferenceEquals(blit.OutFbo, canonicalTarget) ||
                (depth ? !blit.DepthBit : !blit.ColorBit))
                continue;

            using VulkanPreparedResourcePlannerThreadScope scope =
                EnterRecordingResourceScope(plan, in context);
            SwapchainRecordingTarget swapchainTarget = default;
            if (!TryResolvePreparedBlitImage(
                    canonicalTarget,
                    depth ? EReadBufferMode.None : EReadBufferMode.ColorAttachment0,
                    !depth,
                    depth,
                    false,
                    out BlitImageInfo info,
                    false,
                    operations.GetHeader(index).PassIndex,
                    in blit,
                    in swapchainTarget) ||
                info.Image.Handle == 0)
                return false;
            if (!TryResolvePreparedBlitImage(
                    blit.InFbo,
                    blit.ReadBufferMode,
                    !depth,
                    depth,
                    false,
                    out BlitImageInfo sourceInfo,
                    true,
                    operations.GetHeader(index).PassIndex,
                    in blit,
                    in swapchainTarget) ||
                sourceInfo.Image.Handle == 0 ||
                !TryCreateTemporalBlitIdentity(in sourceInfo, out TemporalHistoryResourceIdentity currentSource))
                return false;

            ulong generation = ResourceRuntime.GetPublishedGeneration(
                ObjectType.Image, info.Image.Handle);
            if (generation == 0)
                return false;
            TemporalHistoryResourceIdentity current = new(
                ResourceRuntime, info.Image.Handle, generation,
                (uint)info.AspectMask, info.MipLevel, 1,
                info.BaseArrayLayer, info.LayerCount);
            if (!current.IsValid ||
                (found && (!identity.Matches(in current) ||
                           !sourceIdentity.Matches(in currentSource) ||
                           !ReferenceEquals(recordedTarget, canonicalTarget))))
                return false;

            identity = current;
            recordedTarget = canonicalTarget;
            sourceIdentity = currentSource;
            found = true;
        }

        return found;
    }

    private bool TryCreateTemporalBlitIdentity(
        in BlitImageInfo info,
        out TemporalHistoryResourceIdentity identity)
    {
        identity = default;
        if (info.Image.Handle == 0)
            return false;
        ulong generation = ResourceRuntime.GetPublishedGeneration(
            ObjectType.Image, info.Image.Handle);
        if (generation == 0)
            return false;
        identity = new(ResourceRuntime, info.Image.Handle, generation,
            (uint)info.AspectMask, info.MipLevel, 1,
            info.BaseArrayLayer, info.LayerCount);
        return identity.IsValid;
    }

    private bool TryResolveTemporalResolveProducer(
        FrameOperationStream operations,
        FramePlan plan,
        in TemporalHistorySubmissionCandidate candidate,
        XRFrameBuffer? target,
        in TemporalHistoryResourceIdentity expectedColorSource,
        in TemporalHistoryResourceIdentity expectedAuxiliarySource,
        out XRFrameBuffer? recordedTarget,
        out int originalIndex,
        out TemporalHistoryResourceIdentity colorIdentity,
        out TemporalHistoryResourceIdentity auxiliaryIdentity,
        out uint colorIndex,
        out uint auxiliaryIndex,
        out Extent2D extent)
    {
        recordedTarget = null;
        originalIndex = -1;
        colorIdentity = default;
        auxiliaryIdentity = default;
        colorIndex = 0;
        auxiliaryIndex = 0;
        extent = default;
        if (target is null)
            return true;
        if (!expectedColorSource.IsValid)
            return false;

        for (int index = 0; index < operations.Count; index++)
        {
            if (operations.GetHeader(index).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
                continue;
            ref readonly FrameOpContext context = ref operations.GetContext(index);
            if (!ReferenceEquals(context.PipelineInstance, candidate.PipelineInstance) ||
                context.ResourceRegistry is null || string.IsNullOrEmpty(target.Name) ||
                !context.ResourceRegistry.TryGetFrameBuffer(target.Name, out XRFrameBuffer? canonicalTarget) ||
                canonicalTarget is null ||
                !ReferenceEquals(operations.GetTarget(index), canonicalTarget))
                continue;
            if (originalIndex >= 0)
                return false;

            using VulkanPreparedResourcePlannerThreadScope scope =
                EnterRecordingResourceScope(plan, in context);
            SwapchainRecordingTarget swapchainTarget = default;
            BlitPayload noBlit = default;
            int passIndex = operations.GetHeader(index).PassIndex;
            BlitImageInfo colorInfo = default;
            int matchingColorCount = 0;
            for (uint attachmentIndex = 0;
                 attachmentIndex < DynamicRenderingFormatSignature.MaxColorAttachmentCount;
                 attachmentIndex++)
            {
                EReadBufferMode readMode =
                    (EReadBufferMode)((int)EReadBufferMode.ColorAttachment0 + (int)attachmentIndex);
                if (!TryResolvePreparedBlitImage(canonicalTarget,
                        readMode, true, false, false,
                        out BlitImageInfo attachmentInfo, true, passIndex,
                        in noBlit, in swapchainTarget) ||
                    !TryCreateTemporalBlitIdentity(in attachmentInfo,
                        out TemporalHistoryResourceIdentity attachmentIdentity) ||
                    !attachmentIdentity.Matches(in expectedColorSource))
                    continue;
                matchingColorCount++;
                colorInfo = attachmentInfo;
                colorIdentity = attachmentIdentity;
                colorIndex = attachmentIndex;
            }
            if (matchingColorCount != 1)
                return false;
            if (expectedAuxiliarySource.IsValid)
            {
                int matchingAuxiliaryCount = 0;
                for (uint attachmentIndex = 0;
                     attachmentIndex < DynamicRenderingFormatSignature.MaxColorAttachmentCount;
                     attachmentIndex++)
                {
                    EReadBufferMode readMode =
                        (EReadBufferMode)((int)EReadBufferMode.ColorAttachment0 + (int)attachmentIndex);
                    if (!TryResolvePreparedBlitImage(canonicalTarget,
                            readMode, true, false, false,
                            out BlitImageInfo auxiliaryInfo, true, passIndex,
                            in noBlit, in swapchainTarget) ||
                        !TryCreateTemporalBlitIdentity(in auxiliaryInfo,
                            out TemporalHistoryResourceIdentity currentAuxiliary) ||
                        !currentAuxiliary.Matches(in expectedAuxiliarySource))
                        continue;
                    matchingAuxiliaryCount++;
                    auxiliaryIdentity = currentAuxiliary;
                    auxiliaryIndex = attachmentIndex;
                }
                if (matchingAuxiliaryCount != 1 || auxiliaryIndex == colorIndex)
                    return false;
            }

            recordedTarget = canonicalTarget;
            originalIndex = operations.GetHeader(index).OriginalIndex;
            extent = colorInfo.Extent;
        }

        return originalIndex >= 0;
    }
}
