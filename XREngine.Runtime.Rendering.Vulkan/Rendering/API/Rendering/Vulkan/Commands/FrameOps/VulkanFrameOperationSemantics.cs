using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanFrameOperationSemantics
{
    #region Frame Operation Queue


    internal const ulong FrameSourceMutableDescriptorSignature = 0x4652534D55544453UL;

    internal static bool IsFrameSourceSamplerName(string? name)
        => string.Equals(name, "SourceTexture", StringComparison.Ordinal) ||
            string.Equals(name, "SourceTex", StringComparison.Ordinal) ||
            string.Equals(name, "SourceTexture0", StringComparison.Ordinal) ||
            string.Equals(name, "SourceTexture1", StringComparison.Ordinal);

    internal static bool IsMutableFrameSourceSamplerName(string? name, XRRenderPipelineInstance? pipeline)
    {
        if (IsFrameSourceSamplerName(name))
            return true;

        return !string.IsNullOrWhiteSpace(name) &&
            pipeline is not null &&
            pipeline.TryGetTexture(name, out XRTexture? texture) &&
            texture is not null;
    }

    internal static FrameOp Prepare(FrameOp op, int validatedPassIndex)
    {
        if (op is not TextureUploadFrameOp)
            op.PassIndex = validatedPassIndex;
        FrameOp validatedOp = LowerFrameOpResourceUse(op);
        PublishFrameOpDrawStats(validatedOp);
        return validatedOp;
    }

    /// <summary>
    /// Captures the logical resource sets used by an operation before it enters
    /// the shared frame queue. The identities describe framebuffer attachments,
    /// not submission order or managed framebuffer names, so output planning can
    /// derive producer-to-consumer edges before native command recording.
    /// </summary>
    internal static FrameOp LowerFrameOpResourceUse(FrameOp op)
    {
        ref FrameOpResourceUseList uses = ref op.BeginResourceUseUpdate();
        XRFrameBuffer? output = GetOutputFrameBuffer(op);
        XRFrameBuffer? input = op is BlitOp { InFbo: { } source } ? source : null;

        // An operation's target/input FBOs describe dependencies inside one
        // output pipeline. They are not output-terminal publications. Preserve
        // only semantic cross-output dependency ids supplied by the context;
        // the per-operation resource-use graph below orders internal passes.
        ref readonly FrameOpContext context = ref op.ContextReference;
        AddTypedOperationUses(ref uses, op, output, input, context.ResourceGeneration);

        ComputeDispatchSnapshot? bindings = op switch
        {
            ComputeDispatchOp compute => compute.Snapshot,
            ComputeDispatchIndirectOp computeIndirect => computeIndirect.Snapshot,
            MeshDrawOp draw => draw.Draw.ProgramBindingSnapshot,
            IndirectDrawOp draw => draw.Draw.ProgramBindingSnapshot,
            MeshTaskDispatchIndirectCountOp meshTask => meshTask.ProgramBindingSnapshot,
            _ => null,
        };
        if (bindings is not null)
            AddDescriptorReadUses(ref uses, bindings, context.ResourceGeneration);
        AddDlssUses(ref uses, op, context.ResourceGeneration);
        return op;
    }

    /// <summary>
    /// Lowers a prepared mesh directly into current-frame dependency data.
    /// Retained cohorts never carry these uses across frames because descriptor
    /// and attachment identities can be frame-local even when draw structure is
    /// unchanged.
    /// </summary>
    internal static void LowerMeshDrawResourceUse(
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        ref FrameOpResourceUseList uses)
    {
        uses.Clear();
        AddFrameBufferUses(
            ref uses,
            target ?? context.OutputFrameBuffer,
            context.ResourceGeneration,
            EFrameOpResourceAccess.Write);
        if (draw.ProgramBindingSnapshot is { } bindings)
            AddDescriptorReadUses(
                ref uses,
                bindings,
                context.ResourceGeneration);
    }

    private static XRFrameBuffer? GetOutputFrameBuffer(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo,
            ClearOp clear => clear.Target ?? clear.Context.OutputFrameBuffer,
            MeshDrawOp draw => draw.Target ?? draw.Context.OutputFrameBuffer,
            IndirectDrawOp draw => draw.Target ?? draw.Context.OutputFrameBuffer,
            MeshTaskDispatchIndirectCountOp meshTask => meshTask.Target ?? meshTask.Context.OutputFrameBuffer,
            TransformFeedbackOp transformFeedback => transformFeedback.Target ?? transformFeedback.Context.OutputFrameBuffer,
            AdvancedVisibilityOp visibility
                when visibility.Request.Stage == EAdvancedRenderStage.VisibilityRaster
                => visibility.Target,
            _ => null,
        };

    private static void AddTypedOperationUses(
        ref FrameOpResourceUseList uses,
        FrameOp op,
        XRFrameBuffer? output,
        XRFrameBuffer? input,
        ulong version)
    {
        switch (op)
        {
            case ClearOp clear:
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write,
                    clear.ClearColor, clear.ClearDepth, clear.ClearStencil);
                break;
            case BlitOp blit:
                AddFrameBufferUses(ref uses, input, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported,
                    blit.ColorBit, blit.DepthBit, blit.StencilBit);
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write,
                    blit.ColorBit, blit.DepthBit, blit.StencilBit);
                break;
            case MeshDrawOp:
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write);
                break;
            case TransformFeedbackOp transformFeedback:
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write);
                AddBufferUse(
                    ref uses,
                    transformFeedback.TransformFeedback.Data.FeedbackBuffer,
                    version,
                    EFrameOpResourceAccess.Write | EFrameOpResourceAccess.Imported);
                if (transformFeedback.CounterBuffer is not null)
                    AddBufferUse(
                        ref uses,
                        transformFeedback.CounterBuffer,
                        version,
                        transformFeedback.Operation == EXRTransformFeedbackOperation.DrawIndirectByteCount
                            ? EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported
                            : EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write | EFrameOpResourceAccess.Imported);
                break;
            case IndirectDrawOp indirect:
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write);
                AddBufferUse(ref uses, indirect.IndirectBuffer, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                if (indirect.ParameterBuffer is not null)
                    AddBufferUse(ref uses, indirect.ParameterBuffer, version,
                        EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                AddFrameBufferUses(ref uses, output, version, EFrameOpResourceAccess.Write);
                AddBufferUse(ref uses, meshTask.IndirectBuffer, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                AddBufferUse(ref uses, meshTask.CountBuffer, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                break;
            case PublishFramebufferForSamplingOp publish:
                AddFrameBufferUses(ref uses, publish.FrameBuffer, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                break;
            case BufferCopyOp copy:
                AddBufferUse(ref uses, copy.SourceOwner, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                AddBufferUse(ref uses, copy.DestinationOwner, version,
                    EFrameOpResourceAccess.Write);
                break;
            case ComputeDispatchIndirectOp indirect:
                AddBufferUse(ref uses, indirect.ArgumentOwner, version,
                    EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
                break;
            case AdvancedVisibilityOp visibility:
                if (visibility.Request.Stage == EAdvancedRenderStage.VisibilityRaster ||
                    visibility.Request.Phase ==
                        EAdvancedVisibilityStageBackendPhase.LateRaster)
                {
                    // Preserve both the graph names and the exact realized target.
                    // The former drives graph ordering while the latter freezes
                    // views/formats/extent for native render-scope preparation.
                    AddFrameBufferUses(
                        ref uses,
                        output,
                        version,
                        EFrameOpResourceAccess.Write);
                    AddLogicalResourceUse(
                        ref uses,
                        RenderGraphResourceNames.MakeTexture(visibility.Request.IdentityTargetName),
                        version);
                    AddLogicalResourceUse(
                        ref uses,
                        RenderGraphResourceNames.MakeTexture(visibility.Request.MetadataTargetName),
                        version);
                    AddLogicalResourceUse(
                        ref uses,
                        RenderGraphResourceNames.MakeTexture(visibility.Request.SelectionTargetName),
                        version);
                    AddLogicalResourceUse(
                        ref uses,
                        RenderGraphResourceNames.MakeTexture(visibility.Request.DepthTargetName),
                        version);
                }
                break;
            case TextureUploadFrameOp upload:
                AddTextureUploadUse(ref uses, upload.Upload, version);
                break;
        }
    }

    private static void AddFrameBufferUses(
        ref FrameOpResourceUseList uses,
        XRFrameBuffer? frameBuffer,
        ulong version,
        EFrameOpResourceAccess access,
        bool includeColor = true,
        bool includeDepth = true,
        bool includeStencil = true)
    {
        if (frameBuffer?.Targets is not { Length: > 0 } targets)
            return;
        for (int index = 0; index < targets.Length; index++)
        {
            EFrameBufferAttachment attachment = targets[index].Attachment;
            bool isColor = attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31 ||
                attachment is EFrameBufferAttachment.Back or EFrameBufferAttachment.Front or EFrameBufferAttachment.Left or EFrameBufferAttachment.Right or
                EFrameBufferAttachment.FrontLeft or EFrameBufferAttachment.FrontRight or EFrameBufferAttachment.BackLeft or EFrameBufferAttachment.BackRight;
            bool isDepth = attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;
            bool isStencil = attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;
            if ((!isColor || !includeColor) && (!isDepth || !includeDepth) && (!isStencil || !includeStencil))
                continue;
            uses.Add(
                ComputeResourceIdentity(targets[index].Target),
                version,
                access);
        }
    }

    private static void AddDescriptorReadUses(
        ref FrameOpResourceUseList uses,
        ComputeDispatchSnapshot snapshot,
        ulong version)
    {
        foreach (XRTexture texture in snapshot.Samplers.Values)
            uses.Add(
                ComputeResourceIdentity(texture),
                version,
                EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
        foreach (XRTexture texture in snapshot.SamplersByName.Values)
            uses.Add(
                ComputeResourceIdentity(texture),
                version,
                EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
        foreach (ProgramImageBinding binding in snapshot.Images.Values)
        {
            EFrameOpResourceAccess access = binding.Access switch
            {
                XRRenderProgram.EImageAccess.ReadOnly => EFrameOpResourceAccess.Read,
                XRRenderProgram.EImageAccess.WriteOnly => EFrameOpResourceAccess.Write,
                _ => EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write,
            };
            uses.Add(
                ComputeResourceIdentity(binding.Texture),
                version,
                access | EFrameOpResourceAccess.Imported);
        }
        foreach (VulkanComputeBufferBinding binding in snapshot.Buffers.Values)
            AddComputeBufferUse(ref uses, binding, version);
        foreach (VulkanComputeBufferBinding binding in snapshot.BuffersByName.Values)
            AddComputeBufferUse(ref uses, binding, version);
    }

    private static void AddComputeBufferUse(
        ref FrameOpResourceUseList uses,
        in VulkanComputeBufferBinding binding,
        ulong version)
    {
        // Compute buffers are storage bindings unless reflection has narrowed
        // them to a read-only usage. Conservatively retain both sides of the
        // dependency so producers cannot be scheduled after a dispatch.
        EFrameOpResourceAccess access =
            (binding.UsageFlags & Silk.NET.Vulkan.BufferUsageFlags.StorageBufferBit) != 0
                ? EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write | EFrameOpResourceAccess.Imported
                : EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported;
        AddBufferUse(ref uses, binding.Data, version, access);
    }

    private static void AddBufferUse(
        ref FrameOpResourceUseList uses,
        object buffer,
        ulong version,
        EFrameOpResourceAccess access)
        => uses.Add(ComputeResourceIdentity(buffer), version, access);

    private static void AddLogicalResourceUse(
        ref FrameOpResourceUseList uses,
        string name,
        ulong version)
        => uses.Add(
            ComputeResourceIdentity(name),
            version,
            EFrameOpResourceAccess.Write);

    private static void AddTextureUploadUse(
        ref FrameOpResourceUseList uses,
        VulkanImportedTexturePendingUpload upload,
        ulong version)
        => uses.Add(ComputeResourceIdentity(upload.Texture), version, EFrameOpResourceAccess.Write);

    private static void AddDlssUses(
        ref FrameOpResourceUseList uses,
        FrameOp op,
        ulong version)
    {
        if (op is not DlssUpscaleOp dlss)
        {
            if (op is not DlssFrameGenerationOp frameGeneration)
                return;
            AddStreamlineUse(ref uses, frameGeneration.Depth, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
            AddStreamlineUse(ref uses, frameGeneration.Motion, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
            AddStreamlineUse(ref uses, frameGeneration.HudlessColor, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
            AddStreamlineUse(ref uses, frameGeneration.UiColorAndAlpha, version, EFrameOpResourceAccess.Write);
            return;
        }
        AddStreamlineUse(ref uses, dlss.SourceColor, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
        AddStreamlineUse(ref uses, dlss.Depth, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
        AddStreamlineUse(ref uses, dlss.Motion, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
        AddStreamlineUse(ref uses, dlss.OutputColor, version, EFrameOpResourceAccess.Write);
        if (dlss.Exposure is { } exposure)
            AddStreamlineUse(ref uses, exposure, version, EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);
    }

    private static void AddStreamlineUse(
        ref FrameOpResourceUseList uses,
        in VulkanStreamlineImage image,
        ulong version,
        EFrameOpResourceAccess access)
    {
        if (image.Image.Handle != 0UL)
            uses.Add(image.Image.Handle, version, access);
    }

    private static ulong ComputeResourceIdentity(object resource)
    {
        // Frame operations can refer to a data buffer through either the engine
        // resource or its Vulkan backend wrapper. Both names designate the same
        // underlying allocation, so they must produce one dependency identity.
        // Without this normalization a compute producer recorded with an
        // XRDataBuffer and a mesh-task consumer recorded with its VkDataBuffer
        // have no resource edge despite sharing the same buffer.
        object logicalResource = resource is VkDataBuffer vkDataBuffer
            ? vkDataBuffer.Data
            : resource;
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D4F50524553UL);
        hash.Add(RuntimeHelpers.GetHashCode(logicalResource));
        hash.Add(logicalResource.GetType().GetHashCode());
        ulong result = hash.ToHash();
        return result == 0UL ? 1UL : result;
    }

    internal static void PublishFrameOpDrawStats(FrameOp op)
    {
        if (op.PassIndex == int.MinValue)
            return;

        switch (op)
        {
            case MeshDrawOp meshDraw:
                PendingMeshDraw draw = meshDraw.Draw;
                PublishMeshDrawStats(meshDraw.PassIndex, in draw);
                break;
            case IndirectDrawOp indirectDraw:
                PublishFrameDrawStats(new VulkanFrameDrawStats(
                    SaturateToInt(indirectDraw.DrawCount),
                    MultiDrawCalls: indirectDraw.DrawCount > 0u ? 1 : 0,
                    TrianglesRendered: 0));
                break;
            case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                PublishFrameDrawStats(new VulkanFrameDrawStats(
                    SaturateToInt(meshTaskDispatch.MaxDrawCount),
                    MultiDrawCalls: meshTaskDispatch.MaxDrawCount > 0u ? 1 : 0,
                    TrianglesRendered: 0));
                break;
        }
    }

    internal static void PublishMeshDrawStats(
        int passIndex,
        in PendingMeshDraw draw)
    {
        if (passIndex != int.MinValue)
            PublishFrameDrawStats(draw.Renderer.EstimateFrameDrawStats(draw));
    }

    private static void PublishFrameDrawStats(VulkanFrameDrawStats stats)
    {
        if (stats.DrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(stats.DrawCalls);
        if (stats.MultiDrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(stats.MultiDrawCalls);
        if (stats.TrianglesRendered > 0)
            RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(stats.TrianglesRendered);
    }

    internal static int SaturateToInt(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    internal static int SaturateToInt(ulong value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>Returns an allocation-free producer diagnostic label.</summary>
    internal static string GetFrameOpDiagnosticName(FrameOp op)
        => op switch
        {
            BlitOp => "Blit",
            ClearOp => "Clear",
            TransformFeedbackOp => "TransformFeedback",
            MeshDrawOp => "MeshDraw",
            IndirectDrawOp => "IndirectDraw",
            MeshTaskDispatchIndirectCountOp => "MeshTaskDispatch",
            ComputeDispatchOp => "ComputeDispatch",
            ComputeDispatchIndirectOp => "ComputeDispatchIndirect",
            BufferCopyOp => "BufferCopy",
            SubmissionMarkerOp => "SubmissionMarker",
            MemoryBarrierOp => "MemoryBarrier",
            PublishFramebufferForSamplingOp => "PublishFramebufferForSampling",
            DlssUpscaleOp => "DlssUpscale",
            DlssFrameGenerationOp => "DlssFrameGeneration",
            TextureUploadFrameOp => "TextureUpload",
            QueryOp => "Query",
            _ => "Unknown",
        };

    internal static ulong ComputeFrameOpsSignature(FrameOperationSequence ops)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(ops.Length);

        for (int i = 0; i < ops.Length; i++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(i);
            ref readonly FrameOpContext context = ref ops.GetContext(i);
            hash.Add((int)header.OpCode);
            hash.Add(header.PassIndex);
            hash.Add(header.TargetIdentity);
            hash.Add((int)context.ContextKind);
            hash.Add(context.RecordingFingerprint);
            hash.Add(context.PipelineIdentity);
            hash.Add(context.ViewportIdentity);
            hash.Add(context.OutputFrameBufferIdentity);
            hash.Add(context.OutputTargetIdentity);

            switch (header.OpCode)
            {
                case EVulkanPrimaryPlanNodeKind.Clear:
                    ref readonly ClearPayload clear = ref ops.GetClear(i);
                    hash.Add(clear.ClearColor);
                    hash.Add(clear.ClearDepth);
                    hash.Add(clear.ClearStencil);
                    hash.Add(clear.Color.R);
                    hash.Add(clear.Color.G);
                    hash.Add(clear.Color.B);
                    hash.Add(clear.Color.A);
                    hash.Add(clear.Depth);
                    hash.Add(clear.Stencil);
                    hash.Add(clear.Rect.Offset.X);
                    hash.Add(clear.Rect.Offset.Y);
                    hash.Add(clear.Rect.Extent.Width);
                    hash.Add(clear.Rect.Extent.Height);
                    break;
                case EVulkanPrimaryPlanNodeKind.MeshDraw:
                    PendingMeshDraw draw = ops.GetMeshDraw(i).Draw;
                    hash.Add(draw.Renderer?.GetHashCode() ?? 0);
                    hash.Add(draw.Viewport.X);
                    hash.Add(draw.Viewport.Y);
                    hash.Add(draw.Viewport.Width);
                    hash.Add(draw.Viewport.Height);
                    hash.Add(draw.Scissor.Offset.X);
                    hash.Add(draw.Scissor.Offset.Y);
                    hash.Add(draw.Scissor.Extent.Width);
                    hash.Add(draw.Scissor.Extent.Height);
                    hash.Add(draw.ViewportScissorCount);
                    if (draw.ViewportScissorCount > 1 &&
                        draw.IndexedViewports is { } indexedViewports &&
                        draw.IndexedScissors is { } indexedScissors)
                    {
                        int indexedCount = (int)Math.Min(
                            draw.ViewportScissorCount,
                            (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
                        for (int indexedIndex = 0; indexedIndex < indexedCount; indexedIndex++)
                        {
                            Viewport indexedViewport = indexedViewports[indexedIndex];
                            Rect2D indexedScissor = indexedScissors[indexedIndex];
                            hash.Add(indexedViewport.X);
                            hash.Add(indexedViewport.Y);
                            hash.Add(indexedViewport.Width);
                            hash.Add(indexedViewport.Height);
                            hash.Add(indexedViewport.MinDepth);
                            hash.Add(indexedViewport.MaxDepth);
                            hash.Add(indexedScissor.Offset.X);
                            hash.Add(indexedScissor.Offset.Y);
                            hash.Add(indexedScissor.Extent.Width);
                            hash.Add(indexedScissor.Extent.Height);
                        }
                    }
                    hash.Add(draw.DepthTestEnabled);
                    hash.Add(draw.DepthWriteEnabled);
                    hash.Add((int)draw.DepthCompareOp);
                    hash.Add(draw.StencilTestEnabled);
                    hash.Add(draw.StencilWriteMask);
                    hash.Add((int)draw.ColorWriteMask);
                    hash.Add((int)draw.CullMode);
                    hash.Add((int)draw.FrontFace);
                    hash.Add(draw.BlendEnabled);
                    hash.Add((int)draw.ColorBlendOp);
                    hash.Add((int)draw.AlphaBlendOp);
                    hash.Add((int)draw.SrcColorBlendFactor);
                    hash.Add((int)draw.DstColorBlendFactor);
                    hash.Add((int)draw.SrcAlphaBlendFactor);
                    hash.Add((int)draw.DstAlphaBlendFactor);
                    hash.Add(draw.MaterialOverride?.GetHashCode() ?? 0);
                    hash.Add(draw.Instances);
                    hash.Add((int)draw.BillboardMode);
                    hash.Add(draw.IsStereoPass);
                    hash.Add(draw.UseUnjitteredProjection);
                    hash.Add(draw.PreparedProgramIdentity);
                    hash.Add(draw.PreparedProgram?.BindingId ?? 0u);
                    HashProgramBindingLayoutSnapshot(ref hash, draw.ProgramBindingSnapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.Query:
                    ref readonly QueryPayload query = ref ops.GetQuery(i);
                    hash.Add(query.Query.GetHashCode());
                    hash.Add(query.Descriptor.GetHashCode());
                    hash.Add(query.Query.Ticket.PoolIdentity);
                    hash.Add(query.Query.Ticket.FirstQuery);
                    hash.Add(query.Query.Ticket.QueryCount);
                    hash.Add((int)query.Operation);
                    hash.Add((ulong)query.TimestampStage);
                    hash.Add(query.PointIndex);
                    hash.Add(query.SourceHandles.Length);
                    hash.Add(query.ResultDestination.Handle);
                    hash.Add(query.ResultDestinationOffset);
                    hash.Add(query.ResultStride);
                    hash.Add(query.IncludeAvailability);
                    break;
                case EVulkanPrimaryPlanNodeKind.Blit:
                    ref readonly BlitPayload blit = ref ops.GetBlit(i);
                    hash.Add(blit.InFbo?.GetHashCode() ?? 0);
                    hash.Add(blit.OutFbo?.GetHashCode() ?? 0);
                    hash.Add(blit.InX);
                    hash.Add(blit.InY);
                    hash.Add(blit.InW);
                    hash.Add(blit.InH);
                    hash.Add(blit.OutX);
                    hash.Add(blit.OutY);
                    hash.Add(blit.OutW);
                    hash.Add(blit.OutH);
                    hash.Add((int)blit.ReadBufferMode);
                    hash.Add(blit.ColorBit);
                    hash.Add(blit.DepthBit);
                    hash.Add(blit.StencilBit);
                    hash.Add(blit.LinearFilter);
                    break;
                case EVulkanPrimaryPlanNodeKind.IndirectDraw:
                    ref readonly IndirectDrawPayload indirect = ref ops.GetIndirectDraw(i);
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
                    hash.Add(indirect.DrawCount);
                    hash.Add(indirect.Stride);
                    hash.Add(indirect.ByteOffset);
                    hash.Add(indirect.CountByteOffset);
                    hash.Add(indirect.UseCount);
                    hash.Add(
                        (int)indirect.SecondaryRecordingContract.Eligibility);
                    HashMaterialTableClosure(ref hash, indirect.Draw.ProgramBindingSnapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                    ref readonly MeshTaskDispatchIndirectCountPayload meshTaskDispatch = ref ops.GetMeshTask(i);
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.CountBuffer));
                    hash.Add(meshTaskDispatch.MaxDrawCount);
                    hash.Add(meshTaskDispatch.Stride);
                    hash.Add(meshTaskDispatch.ByteOffset);
                    hash.Add(meshTaskDispatch.CountByteOffset);
                    HashMaterialTableClosure(ref hash, meshTaskDispatch.ProgramBindingSnapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.MemoryBarrier:
                    ref readonly MemoryBarrierPayload barrier = ref ops.GetMemoryBarrier(i);
                    hash.Add((int)barrier.Mask);
                    break;
                case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling:
                    ref readonly PublishFramebufferPayload publish = ref ops.GetPublishedFramebuffer(i);
                    hash.Add(publish.FrameBuffer.GetHashCode());
                    break;
                case EVulkanPrimaryPlanNodeKind.DlssUpscale:
                    ref readonly DlssUpscalePayload dlss = ref ops.GetDlssUpscale(i);
                    hash.Add(dlss.Session.GetHashCode());
                    hash.Add(dlss.SourceColor.Image.Handle);
                    hash.Add(dlss.Depth.Image.Handle);
                    hash.Add(dlss.Motion.Image.Handle);
                    hash.Add(dlss.OutputColor.Image.Handle);
                    hash.Add(dlss.Exposure?.Image.Handle ?? 0UL);
                    hash.Add(dlss.Parameters.InputWidth);
                    hash.Add(dlss.Parameters.InputHeight);
                    hash.Add(dlss.Parameters.OutputWidth);
                    hash.Add(dlss.Parameters.OutputHeight);
                    hash.Add(dlss.Parameters.FrameIndex);
                    hash.Add(dlss.Parameters.ResetHistory);
                    hash.Add(dlss.Parameters.OutputHdr);
                    hash.Add((int)dlss.Parameters.DlssQuality);
                    break;
                case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration:
                    ref readonly DlssFrameGenerationPayload dlssFrameGeneration = ref ops.GetDlssFrameGeneration(i);
                    hash.Add(dlssFrameGeneration.Session.GetHashCode());
                    HashStreamlineImageIdentity(ref hash, dlssFrameGeneration.Depth);
                    HashStreamlineImageIdentity(ref hash, dlssFrameGeneration.Motion);
                    HashStreamlineImageIdentity(ref hash, dlssFrameGeneration.HudlessColor);
                    HashStreamlineImageIdentity(ref hash, dlssFrameGeneration.UiColorAndAlpha);
                    hash.Add(dlssFrameGeneration.Parameters.InputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.InputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.OutputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.FrameIndex);
                    hash.Add(dlssFrameGeneration.Parameters.ResetHistory);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHdr);
                    break;
                case EVulkanPrimaryPlanNodeKind.TransformFeedback:
                    ref readonly TransformFeedbackPayload transformFeedback = ref ops.GetTransformFeedback(i);
                    hash.Add(transformFeedback.TransformFeedback.GetHashCode());
                    hash.Add((int)transformFeedback.Operation);
                    hash.Add(transformFeedback.CounterBuffer?.GetHashCode() ?? 0);
                    hash.Add(transformFeedback.FeedbackBufferOffset);
                    hash.Add(transformFeedback.FeedbackBufferSize ?? 0ul);
                    hash.Add(transformFeedback.CounterBufferOffset);
                    hash.Add(transformFeedback.CounterOffset);
                    hash.Add(transformFeedback.VertexStride);
                    hash.Add(transformFeedback.InstanceCount);
                    hash.Add(transformFeedback.FirstInstance);
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                    ref readonly ComputeDispatchPayload compute = ref ops.GetComputeDispatch(i);
                    hash.Add(compute.Program.GetHashCode());
                    hash.Add(compute.GroupsX);
                    hash.Add(compute.GroupsY);
                    hash.Add(compute.GroupsZ);
                    HashProgramBindingLayoutSnapshot(ref hash, compute.Snapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                    ref readonly ComputeDispatchIndirectPayload computeIndirect = ref ops.GetComputeDispatchIndirect(i);
                    hash.Add(computeIndirect.Program.GetHashCode());
                    hash.Add(computeIndirect.ArgumentBuffer.Handle);
                    hash.Add(computeIndirect.ArgumentOffset);
                    HashProgramBindingLayoutSnapshot(ref hash, computeIndirect.Snapshot);
                    break;
                case EVulkanPrimaryPlanNodeKind.BufferCopy:
                    ref readonly BufferCopyPayload copy = ref ops.GetBufferCopy(i);
                    hash.Add(copy.SourceBuffer.Handle);
                    hash.Add(copy.SourceOffset);
                    hash.Add(copy.DestinationBuffer.Handle);
                    hash.Add(copy.DestinationOffset);
                    hash.Add(copy.ByteCount);
                    hash.Add(copy.RequireGpuWriteVisibility);
                    hash.Add(copy.DiagnosticReceipt?.Sequence ?? 0UL);
                    break;
                case EVulkanPrimaryPlanNodeKind.SubmissionMarker:
                    // The fence object is CPU-side submission state and is rebound
                    // whenever a cached primary is reused. Marker position remains
                    // part of the structural signature because recording it closes
                    // the active render pass.
                    break;
                case EVulkanPrimaryPlanNodeKind.TextureUpload:
                    VulkanImportedTexturePendingUpload upload = ops.GetTextureUpload(i).Upload;
                    hash.Add(upload.PublicationToken);
                    hash.Add(upload.Request.StreamingGeneration);
                    hash.Add(upload.Image.Handle);
                    hash.Add(upload.ImageView.Handle);
                    hash.Add(upload.Sampler.Handle);
                    hash.Add(upload.Extent.Width);
                    hash.Add(upload.Extent.Height);
                    hash.Add(upload.MipLevels);
                    hash.Add((ulong)Math.Max(upload.CommittedBytes, 0L));
                    hash.Add(upload.StagingResources.Length);
                    break;
            }
        }

        return hash.ToHash();
    }

    private static void HashStreamlineImageIdentity(
        ref FrameOpSignatureHasher hash,
        in VulkanStreamlineImage image)
    {
        hash.Add(image.Image.Handle);
        hash.Add(image.Memory.Handle);
        hash.Add(image.View.Handle);
        hash.Add((int)image.Layout);
        hash.Add((int)image.Format);
        hash.Add((int)image.Usage);
        hash.Add((int)image.Aspect);
        hash.Add(image.Width);
        hash.Add(image.Height);
    }

    private static ulong ComputeCommandBufferDataBufferSignature(VkDataBuffer? buffer)
    {
        FrameOpSignatureHasher hash = new();
        if (buffer is null)
        {
            hash.Add(0UL);
            return hash.ToHash();
        }

        hash.Add(buffer.GetHashCode());
        hash.Add(buffer.BufferHandle?.Handle ?? 0UL);
        hash.Add(buffer.AllocatedByteSize);
        hash.Add(buffer.UploadedByteCount);
        hash.Add(buffer.HasPendingUpload);
        hash.Add(buffer.Data.Length);
        hash.Add((int)buffer.Data.Target);
        hash.Add((ulong)buffer.LastUsageFlags);
        return hash.ToHash();
    }

    private static void HashProgramBindingSnapshot(
        ref FrameOpSignatureHasher hash,
        ComputeDispatchSnapshot? snapshot,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        if (snapshot is null)
        {
            hash.Add(0);
            return;
        }

        hash.Add(1);
        hash.Add(HashSamplerUnitBindings(snapshot.Samplers, snapshot.SamplerNamesByUnit, snapshot.DescriptorSignatures, pipeline, includeMutableFrameSourceDescriptors));
        hash.Add(HashSamplerNameBindings(snapshot.SamplersByName, snapshot.DescriptorSignatures, pipeline, includeMutableFrameSourceDescriptors));
        hash.Add(HashImageBindings(snapshot.Images, snapshot.DescriptorSignatures));
        hash.Add(HashBufferBindings(snapshot.Buffers));
    }

    private static void HashProgramBindingLayoutSnapshot(ref FrameOpSignatureHasher hash, ComputeDispatchSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            hash.Add(0);
            return;
        }

        hash.Add(1);
        HashMaterialTableClosure(ref hash, snapshot);
        if (snapshot.HasPublishedBindingLayoutSignatures)
        {
            hash.Add(snapshot.SamplerUnitBindingLayoutSignature);
            hash.Add(snapshot.SamplerNameBindingLayoutSignature);
            hash.Add(snapshot.ImageBindingLayoutSignature);
            hash.Add(snapshot.BufferBindingLayoutSignature);
            return;
        }

        hash.Add(VulkanFrameOpSnapshotSignatures.HashSamplerUnitBindingLayout(snapshot.Samplers, snapshot.SamplerNamesByUnit));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashSamplerNameBindingLayout(snapshot.SamplersByName));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashImageBindingLayout(snapshot.Images));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashBufferBindingLayout(snapshot.Buffers));
    }

    private static void HashMaterialTableClosure(ref FrameOpSignatureHasher hash, ComputeDispatchSnapshot? snapshot)
    {
        if (snapshot?.MaterialTablePublication is not { } publication)
            return;
        hash.Add(publication.OwnerId);
        hash.Add(publication.DescriptorClosureGeneration);
        hash.Add(publication.RowByteStride);
        hash.Add(publication.RowCount);
    }

    private static ulong HashUniformBindingLayout(Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in uniforms)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add((int)pair.Value.Type);
            item.Add(pair.Value.IsArray);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(uniforms.Count, xor, sum);
    }

    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in uniforms)
        {
            HashCode item = new();
            item.Add(pair.Key, StringComparer.Ordinal);
            item.Add((int)pair.Value.Type);
            item.Add(pair.Value.IsArray);
            HashUniformValue(ref item, pair.Value);
            AddUnorderedItemHash(ref xor, ref sum, unchecked((ulong)item.ToHashCode()));
        }

        return FinishUnorderedHash(uniforms.Count, xor, sum);
    }

    /// <summary>
    /// Hashes only callback-owned numeric bindings. The immutable snapshot
    /// producer pays this bounded cost once, while command-buffer reuse compares
    /// the resulting publication without re-running callbacks or scanning UBOs.
    /// </summary>
    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms,
        HashSet<string> selectedNames)
    {
        ulong xor = 0;
        ulong sum = 0;
        int count = 0;
        foreach (string name in selectedNames)
        {
            if (!uniforms.TryGetValue(
                    name,
                    out ProgramUniformValue value))
            {
                continue;
            }

            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add((int)value.Type);
            item.Add(value.IsArray);
            HashUniformValue(ref item, value);
            AddUnorderedItemHash(
                ref xor,
                ref sum,
                unchecked((ulong)item.ToHashCode()));
            count++;
        }

        return FinishUnorderedHash(count, xor, sum);
    }

    /// <summary>
    /// Hashes only engine-owned uniforms in the requested frequency groups.
    /// Camera, time, viewport, and clip-space values remain draw-owned and can
    /// therefore be excluded from persistent program-binding artifacts.
    /// </summary>
    internal static ulong HashUniformBindings(
        Dictionary<string, ProgramUniformValue> uniforms,
        EUniformRequirements selectedRequirements)
    {
        ulong xor = 0;
        ulong sum = 0;
        int count = 0;
        foreach ((string name, ProgramUniformValue value) in uniforms)
        {
            EUniformRequirements requirement =
                UniformRequirementsDetection.GetRequirement(name);
            if ((requirement & selectedRequirements) == 0)
                continue;

            HashCode item = new();
            item.Add(name, StringComparer.Ordinal);
            item.Add((int)value.Type);
            item.Add(value.IsArray);
            HashUniformValue(ref item, value);
            AddUnorderedItemHash(
                ref xor,
                ref sum,
                unchecked((ulong)item.ToHashCode()));
            count++;
        }

        return FinishUnorderedHash(count, xor, sum);
    }

    internal static ulong HashSamplerUnitBindings(
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            bool mutableFrameSource = samplerNamesByUnit.TryGetValue(pair.Key, out string? samplerName) &&
                IsMutableFrameSourceSamplerName(samplerName, pipeline);
            if (!includeMutableFrameSourceDescriptors && mutableFrameSource)
                AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);
            else
                descriptorSignatures.AddSignature(ref item, pair.Value);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashSamplerNameBindings(
        Dictionary<string, XRTexture> samplers,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures,
        XRRenderPipelineInstance? pipeline = null,
        bool includeMutableFrameSourceDescriptors = false)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            if (!includeMutableFrameSourceDescriptors && IsMutableFrameSourceSamplerName(pair.Key, pipeline))
                AddFrameSourceTextureDescriptorSignature(ref item, pair.Value);
            else
                descriptorSignatures.AddSignature(ref item, pair.Value);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    internal static ulong HashImageBindings(
        Dictionary<uint, ProgramImageBinding> images,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in images)
        {
            ProgramImageBinding binding = pair.Value;
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            descriptorSignatures.AddSignature(ref item, binding.Texture);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    private static void AddFrameSourceTextureDescriptorSignature(ref FrameOpSignatureHasher hash, XRTexture? texture)
    {
        hash.Add(FrameSourceMutableDescriptorSignature);
    }

    private static ulong ComputeTextureDescriptorSignature(
        XRTexture? texture,
        VulkanTextureDescriptorSignaturePlan descriptorSignatures)
        => descriptorSignatures.ComputeSignature(texture);

    internal static ulong HashBufferBindings(Dictionary<uint, VulkanComputeBufferBinding> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (var pair in buffers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(pair.Value.Data.GetHashCode());
            item.Add(pair.Value.Buffer.Handle);
            item.Add(pair.Value.Range);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    internal static void AddUnorderedItemHash(ref ulong xor, ref ulong sum, ulong itemHash)
    {
        unchecked
        {
            xor ^= itemHash;
            sum += BitOperations.RotateLeft(itemHash, (int)(itemHash & 31));
        }
    }

    internal static ulong FinishUnorderedHash(int count, ulong xor, ulong sum)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(count);
        hash.Add(xor);
        hash.Add(sum);
        return hash.ToHash();
    }

    private static void HashUniformValue(ref HashCode hash, object? value)
    {
        if (value is null)
        {
            hash.Add(0);
            return;
        }

        if (value is Array array)
        {
            hash.Add(array.Length);
            HashUniformArray(ref hash, array);
            return;
        }

        hash.Add(value);
    }

    private static void HashUniformValue(ref HashCode hash, ProgramUniformValue value)
    {
        if (value.ReferenceValue is { } referenceValue)
        {
            HashUniformValue(ref hash, referenceValue);
            return;
        }

        if (!value.HasInlineValue)
        {
            hash.Add(0);
            return;
        }

        switch (value.Type)
        {
            case EShaderVarType._float:
                hash.Add(value.Float);
                break;
            case EShaderVarType._int:
            case EShaderVarType._bool:
                hash.Add(value.Int);
                break;
            case EShaderVarType._uint:
                hash.Add(value.UInt);
                break;
            case EShaderVarType._double:
                hash.Add(value.Double);
                break;
            case EShaderVarType._vec2:
                hash.Add(value.Vector2);
                break;
            case EShaderVarType._vec3:
                hash.Add(value.Vector3);
                break;
            case EShaderVarType._vec4:
                hash.Add(value.Vector4);
                break;
            case EShaderVarType._mat4:
                hash.Add(value.Matrix4x4);
                break;
            case EShaderVarType._dvec2:
                hash.Add(new DVector2(value.DVector4.X, value.DVector4.Y));
                break;
            case EShaderVarType._dvec3:
                hash.Add(new DVector3(value.DVector4.X, value.DVector4.Y, value.DVector4.Z));
                break;
            case EShaderVarType._dvec4:
                hash.Add(value.DVector4);
                break;
            case EShaderVarType._ivec2:
                hash.Add(new IVector2(value.IVector4.X, value.IVector4.Y));
                break;
            case EShaderVarType._ivec3:
                hash.Add(new IVector3(value.IVector4.X, value.IVector4.Y, value.IVector4.Z));
                break;
            case EShaderVarType._ivec4:
                hash.Add(value.IVector4);
                break;
            case EShaderVarType._uvec2:
                hash.Add(new UVector2(value.UVector4.X, value.UVector4.Y));
                break;
            case EShaderVarType._uvec3:
                hash.Add(new UVector3(value.UVector4.X, value.UVector4.Y, value.UVector4.Z));
                break;
            case EShaderVarType._uvec4:
                hash.Add(value.UVector4);
                break;
            default:
                hash.Add(0);
                break;
        }
    }

    private static void HashUniformArray(ref HashCode hash, Array array)
    {
        switch (array)
        {
            case float[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case int[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case uint[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case bool[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector2[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector3[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Vector4[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            case Matrix4x4[] values:
                for (int i = 0; i < values.Length; i++)
                    hash.Add(values[i]);
                return;
            default:
                for (int i = 0; i < array.Length; i++)
                    HashUniformValue(ref hash, array.GetValue(i));
                return;
        }
    }

    #endregion
}
