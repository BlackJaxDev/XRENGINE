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

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    #region Frame Operation Queue


    private const int FrameOpKindUnknown = 0;
    private const int FrameOpKindClear = 1;
    private const int FrameOpKindMeshDraw = 2;
    private const int FrameOpKindBlit = 3;
    private const int FrameOpKindIndirectDraw = 4;
    private const int FrameOpKindMeshTaskDispatchIndirectCount = 5;
    private const int FrameOpKindMemoryBarrier = 6;
    private const int FrameOpKindDlssUpscale = 7;
    private const int FrameOpKindDlssFrameGeneration = 8;
    private const int FrameOpKindTransformFeedback = 9;
    private const int FrameOpKindComputeDispatch = 10;
    private const int FrameOpKindTextureUpload = 11;
    private const int FrameOpKindQuery = 12;
    private const int FrameOpKindPublishFramebufferForSampling = 13;
    private const int FrameOpKindComputeDispatchIndirect = 14;
    private const int FrameOpKindBufferCopy = 15;
    private const int FrameOpKindSubmissionMarker = 16;

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

    internal void EnqueueFrameOp(FrameOp op)
    {
        FrameOp validatedOp = LowerFrameOpResourceUse(EnsureValidFrameOpPassIndex(op));
        PublishFrameOpDrawStats(validatedOp);

        FrameOpCapture? capture = _frameOperationQueue.CurrentThread.Capture;
        if (capture is not null)
        {
            if (capture.ExcludeTextureUploads && validatedOp is TextureUploadFrameOp)
            {
                using (_frameOperationQueue.SyncRoot.EnterScope())
                    _frameOperationQueue.Pending.Add(validatedOp);
            }
            else
            {
                capture.Add(validatedOp);
            }

            return;
        }

        using (_frameOperationQueue.SyncRoot.EnterScope())
            _frameOperationQueue.Pending.Add(validatedOp);
    }

    /// <summary>
    /// Captures the logical resource sets used by an operation before it enters
    /// the shared frame queue. The identities describe framebuffer attachments,
    /// not submission order or managed framebuffer names, so output planning can
    /// derive producer-to-consumer edges before native command recording.
    /// </summary>
    private static FrameOp LowerFrameOpResourceUse(FrameOp op)
    {
        FrameOpResourceUseList uses = default;
        XRFrameBuffer? output = GetOutputFrameBuffer(op);
        XRFrameBuffer? input = op is BlitOp { InFbo: { } source } ? source : null;

        FrameOpContext context = op.Context with
        {
            OutputProducerDependencySetId = ComputeOutputResourceSetId(output, op.Context),
            OutputConsumerDependencySetId = input is null
                ? 0UL
                : ComputeOutputResourceSetId(input, op.Context),
        };
        op.Context = context;
        AddTypedOperationUses(ref uses, op, output, input, context.ResourceGeneration);

        ComputeDispatchSnapshot? bindings = op switch
        {
            ComputeDispatchOp compute => compute.Snapshot,
            ComputeDispatchIndirectOp computeIndirect => computeIndirect.Snapshot,
            MeshDrawOp draw => draw.Draw.ProgramBindingSnapshot,
            IndirectDrawOp draw => draw.Draw.ProgramBindingSnapshot,
            _ => null,
        };
        if (bindings is not null)
            AddDescriptorReadUses(ref uses, bindings, context.ResourceGeneration);
        AddDlssUses(ref uses, op, context.ResourceGeneration);
        op.SetResourceUses(uses);
        return op;
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
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D4F50524553UL);
        hash.Add(RuntimeHelpers.GetHashCode(resource));
        hash.Add(resource.GetType().GetHashCode());
        ulong result = hash.ToHash();
        return result == 0UL ? 1UL : result;
    }

    private static ulong ComputeOutputResourceSetId(
        XRFrameBuffer? frameBuffer,
        in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D45534F5552UL);
        if (frameBuffer?.Targets is { Length: > 0 } targets)
        {
            for (int index = 0; index < targets.Length; index++)
            {
                var attachment = targets[index];
                hash.Add(RuntimeHelpers.GetHashCode(attachment.Target));
                hash.Add((int)attachment.Attachment);
                hash.Add(attachment.MipLevel);
                hash.Add(attachment.LayerIndex);
            }
        }
        else
        {
            // Default/OpenXR presentation targets have no managed framebuffer
            // attachment list. Their target identity is still the resource
            // contract captured by the render context, never an order token.
            hash.Add(context.OutputTargetIdentity);
            hash.Add(context.OutputFrameBufferIdentity);
            hash.Add((int)context.ContextKind);
        }

        ulong result = hash.ToHash();
        return result == 0UL ? 1UL : result;
    }

    /// <summary>
    /// Begins a transactional ordered-work batch while preserving any outer
    /// command-chain capture active on the render thread.
    /// </summary>
    public bool TryBeginOrderedComputeBatch()
    {
        if (_frameOperationQueue.CurrentThread.OrderedComputeBatchCapture is not null)
            return false;

        FrameOpCapture capture = _frameOperationQueue.CurrentThread.OrderedComputeBatchCaptureScratch ??= new FrameOpCapture();
        capture.Begin(_frameOperationQueue.CurrentThread.Capture, excludeTextureUploads: false);
        _frameOperationQueue.CurrentThread.OrderedComputeBatchCapture = capture;
        _frameOperationQueue.CurrentThread.Capture = capture;
        return true;
    }

    /// <summary>Atomically appends every captured operation to the parent command stream.</summary>
    public void CommitOrderedComputeBatch()
    {
        FrameOpCapture capture = EndOrderedComputeBatch();
        FrameOpCapture? previous = capture.Previous;
        if (previous is not null)
        {
            for (int i = 0; i < capture.Count; ++i)
                previous.Add(capture.Buffer[i]);
            return;
        }

        using (_frameOperationQueue.SyncRoot.EnterScope())
            for (int i = 0; i < capture.Count; ++i)
                _frameOperationQueue.Pending.Add(capture.Buffer[i]);
    }

    /// <summary>Discards every operation and fails any completion marker in the batch.</summary>
    public void RollbackOrderedComputeBatch()
    {
        FrameOpCapture capture = EndOrderedComputeBatch();
        for (int i = 0; i < capture.Count; ++i)
            if (capture.Buffer[i] is SubmissionMarkerOp marker)
                marker.Fence.Fail();
    }

    private FrameOpCapture EndOrderedComputeBatch()
    {
        FrameOpCapture capture = _frameOperationQueue.CurrentThread.OrderedComputeBatchCapture
            ?? throw new InvalidOperationException("No ordered compute batch is active on this thread.");
        _frameOperationQueue.CurrentThread.Capture = capture.Previous;
        _frameOperationQueue.CurrentThread.OrderedComputeBatchCapture = null;
        return capture;
    }

    private bool TryGetLastFrameOpForTarget(XRFrameBuffer target, out FrameOp op)
    {
        FrameOpCapture? capture = _frameOperationQueue.CurrentThread.Capture;
        if (capture is not null)
        {
            for (int i = capture.Count - 1; i >= 0; i--)
            {
                FrameOp candidate = capture.Buffer[i];
                if (FrameOpTargets(candidate, target))
                {
                    op = candidate;
                    return true;
                }
            }
        }

        using (_frameOperationQueue.SyncRoot.EnterScope())
        {
            for (int i = _frameOperationQueue.Pending.Count - 1; i >= 0; i--)
            {
                FrameOp candidate = _frameOperationQueue.Pending[i];
                if (FrameOpTargets(candidate, target))
                {
                    op = candidate;
                    return true;
                }
            }
        }

        op = null!;
        return false;
    }

    private static bool FrameOpTargets(FrameOp op, XRFrameBuffer target)
        => op is not PublishFramebufferForSamplingOp &&
           ReferenceEquals(op.Target, target);

    internal bool EnqueueOcclusionQueryBegin(XRRenderQuery query)
        => query.Descriptor.Kind == ERenderQueryKind.Occlusion &&
           EnqueueRenderQueryOp(query, ERenderQueryOperation.Begin);

    internal bool EnqueueOcclusionQueryEnd(XRRenderQuery query)
        => query.Descriptor.Kind == ERenderQueryKind.Occlusion &&
           EnqueueRenderQueryOp(query, ERenderQueryOperation.End);

    internal bool EnqueueRenderQueryBegin(XRRenderQuery query)
        => EnqueueRenderQueryOp(query, ERenderQueryOperation.Begin);

    internal bool EnqueueRenderQueryEnd(XRRenderQuery query)
        => EnqueueRenderQueryOp(query, ERenderQueryOperation.End);

    internal bool EnqueueTimestampQuery(
        XRRenderQuery query,
        Silk.NET.Vulkan.PipelineStageFlags2 stage = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
        uint pointIndex = 0u)
        => query.Descriptor.Kind is ERenderQueryKind.Timestamp or ERenderQueryKind.ElapsedTime &&
           EnqueueRenderQueryOp(query, ERenderQueryOperation.WriteTimestamp, stage, pointIndex);

    internal bool EnqueueRenderQueryReset(XRRenderQuery query)
        => EnqueueRenderQueryOp(query, ERenderQueryOperation.Reset);

    internal bool EnqueueRenderQueryProperties(
        XRRenderQuery query,
        ReadOnlyMemory<ulong> sourceHandles)
        => sourceHandles.Length != 0 &&
           EnqueueRenderQueryOp(
               query,
               ERenderQueryOperation.WriteProperties,
               sourceHandles: sourceHandles);

    internal bool EnqueueRenderQueryResultCopy(
        XRRenderQuery query,
        Silk.NET.Vulkan.Buffer destination,
        ulong destinationOffset,
        ulong stride,
        bool includeAvailability = true)
        => destination.Handle != 0ul &&
           EnqueueRenderQueryOp(
               query,
               ERenderQueryOperation.CopyResults,
               resultDestination: destination,
               resultDestinationOffset: destinationOffset,
               resultStride: stride,
               includeAvailability: includeAvailability);

    // Tracks whether the calling thread is currently between an occlusion QueryOp
    // Begin and End enqueue. Mesh draws enqueued inside the bracket (proxy AABB
    // draws) are marked PreserveSubmissionOrder. The render-graph sorter partitions
    // each pass at query boundaries so draws cannot cross a bracket while unrelated
    // opaque regions retain canonical batching order.
    internal bool IsInRenderQueryBracket => _frameOperationQueue.CurrentThread.RenderQueryBracketDepth > 0;

    internal bool IsInOcclusionQueryBracket => IsInRenderQueryBracket;

    private bool EnqueueRenderQueryOp(
        XRRenderQuery query,
        ERenderQueryOperation operation,
        Silk.NET.Vulkan.PipelineStageFlags2 timestampStage = Silk.NET.Vulkan.PipelineStageFlags2.AllCommandsBit,
        uint pointIndex = 0u,
        ReadOnlyMemory<ulong> sourceHandles = default,
        Silk.NET.Vulkan.Buffer resultDestination = default,
        ulong resultDestinationOffset = 0ul,
        ulong resultStride = 0ul,
        bool includeAvailability = true)
    {
        if (RuntimeEngine.Rendering.State.CurrentRenderingPipeline is null)
            return false;

        if (query.Descriptor.Kind == ERenderQueryKind.Occlusion &&
            RenderDiagnosticsFlags.VkSkipOcclusionQueryOps &&
            (operation == ERenderQueryOperation.Begin || _frameOperationQueue.CurrentThread.RenderQueryBracketDepth == 0))
        {
            Debug.VulkanWarningEvery(
                "Vulkan.OcclusionQueryOpsSkipped",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping occlusion QueryOp {0} for command-chain ceiling diagnostics ({1}=1). Query results remain stale/conservative.",
                operation,
                XREngineEnvironmentVariables.VkSkipOcclusionQueryOps);
            return false;
        }

        VkRenderQuery? vkQuery = GenericToAPI<VkRenderQuery>(query);
        if (vkQuery is null)
            return false;

        FrameOpContext context = CaptureFrameOpContext();
        int passIndex = EnsureValidPassIndex(
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            "Query",
            context.PassMetadata);

        EnqueueFrameOp(new QueryOp(
            passIndex,
            ResolveCurrentFrameOpDrawTarget(),
            vkQuery,
            query.Descriptor,
            operation,
            context,
            timestampStage,
            pointIndex,
            sourceHandles,
            resultDestination,
            resultDestinationOffset,
            resultStride,
            includeAvailability));

        if (operation == ERenderQueryOperation.Begin)
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth++;
        else if (operation == ERenderQueryOperation.End && _frameOperationQueue.CurrentThread.RenderQueryBracketDepth > 0)
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth--;

        return true;
    }

    internal FrameOp[] CaptureFrameOpsExcludingTextureUploads(Action emitFrameOps, out ulong signature)
        => CaptureFrameOps(emitFrameOps, excludeTextureUploads: true, out signature);

    private FrameOp[] CaptureFrameOps(Action emitFrameOps, bool excludeTextureUploads, out ulong signature)
    {
        FrameOpCapture? previous = _frameOperationQueue.CurrentThread.Capture;
        FrameOpCapture capture = RentFrameOpCapture(previous, excludeTextureUploads);
        _frameOperationQueue.CurrentThread.Capture = capture;
        try
        {
            emitFrameOps();
        }
        finally
        {
            _frameOperationQueue.CurrentThread.Capture = previous;
        }

        int opCount = capture.Count;
        if (opCount == 0)
        {
            signature = 0;
            return Array.Empty<FrameOp>();
        }

        FrameOp[] ops = GetThreadFrameOpCaptureBuffer(opCount);
        Array.Copy(capture.Buffer, ops, opCount);
        signature = ComputeFrameOpsSignature(ops);
        return ops;
    }

    private FrameOpCapture RentFrameOpCapture(FrameOpCapture? previous, bool excludeTextureUploads)
    {
        FrameOpCapture capture;
        if (previous is null)
        {
            capture = _frameOperationQueue.CurrentThread.CaptureScratch ??= new FrameOpCapture();
        }
        else
        {
            // Nested capture scopes are not expected in steady-state recording; keep them correct
            // without complicating the common single-scope hot path.
            capture = new FrameOpCapture();
        }

        capture.Begin(previous, excludeTextureUploads);
        return capture;
    }

    private FrameOp[] GetThreadFrameOpCaptureBuffer(int opCount)
    {
        Dictionary<int, FrameOp[]> buffersByCount = _frameOperationQueue.CurrentThread.CaptureBuffersByCount;
        if (!buffersByCount.TryGetValue(opCount, out FrameOp[]? buffer))
        {
            buffer = new FrameOp[opCount];
            buffersByCount.Add(opCount, buffer);
        }

        return buffer;
    }

    private void ReleaseCurrentThreadFrameOpCaptureCaches()
        => _frameOperationQueue.ReleaseCurrentThread();

    private static void PublishFrameOpDrawStats(FrameOp op)
    {
        if (op.PassIndex == int.MinValue)
            return;

        switch (op)
        {
            case MeshDrawOp meshDraw:
                PublishFrameDrawStats(meshDraw.Draw.Renderer.EstimateFrameDrawStats(meshDraw.Draw));
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

    private FrameOp EnsureValidFrameOpPassIndex(FrameOp op)
    {
        if (op is TextureUploadFrameOp)
            return op;

        int validatedPassIndex = EnsureValidPassIndex(op.PassIndex, GetFrameOpDiagnosticName(op), op.Context.PassMetadata);
        if (validatedPassIndex == op.PassIndex)
            return op;

        // Frame operations are owned by the current frame and intentionally mutable.
        // Cloning a MeshDrawOp copies its large captured draw payload and made a
        // command-buffer refresh allocate once per visible draw.
        op.PassIndex = validatedPassIndex;
        return op;
    }

    internal FrameOp[] DrainFrameOps()
        => DrainFrameOps(out _);

    internal FrameOp[] DrainFrameOps(out ulong signature)
        => DrainFrameOps(out signature, computeSignature: true);

    internal FrameOp[] DrainFrameOps(out ulong signature, bool computeSignature)
    {
        using (_frameOperationQueue.SyncRoot.EnterScope())
        {
            if (_frameOperationQueue.Pending.Count == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            int opCount = _frameOperationQueue.Pending.Count;
            if (_frameOperationQueue.DrainedFrameOpsBuffer.Length != opCount)
                _frameOperationQueue.DrainedFrameOpsBuffer = new FrameOp[opCount];

            _frameOperationQueue.Pending.CopyTo(_frameOperationQueue.DrainedFrameOpsBuffer);
            _frameOperationQueue.Pending.Clear();
            signature = computeSignature ? ComputeFrameOpsSignature(_frameOperationQueue.DrainedFrameOpsBuffer) : 0;
            return _frameOperationQueue.DrainedFrameOpsBuffer;
        }
    }

    internal FrameOp[] DrainFrameOpsSplitTextureUploads(
        out FrameOp[] textureUploadOps,
        out ulong signature,
        bool computeSignature)
    {
        using (_frameOperationQueue.SyncRoot.EnterScope())
        {
            if (_frameOperationQueue.Pending.Count == 0)
            {
                textureUploadOps = Array.Empty<FrameOp>();
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            int opCount = _frameOperationQueue.Pending.Count;
            int uploadCount = 0;
            for (int i = 0; i < opCount; i++)
            {
                if (_frameOperationQueue.Pending[i] is TextureUploadFrameOp)
                    uploadCount++;
            }

            if (uploadCount == 0)
            {
                if (_frameOperationQueue.DrainedFrameOpsBuffer.Length != opCount)
                    _frameOperationQueue.DrainedFrameOpsBuffer = new FrameOp[opCount];

                _frameOperationQueue.Pending.CopyTo(_frameOperationQueue.DrainedFrameOpsBuffer);
                _frameOperationQueue.Pending.Clear();
                textureUploadOps = Array.Empty<FrameOp>();
                signature = computeSignature ? ComputeFrameOpsSignature(_frameOperationQueue.DrainedFrameOpsBuffer) : 0;
                return _frameOperationQueue.DrainedFrameOpsBuffer;
            }

            int staticCount = opCount - uploadCount;
            if (_frameOperationQueue.DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            if (staticCount == 0)
            {
                for (int i = 0; i < opCount; i++)
                    _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer[i] = _frameOperationQueue.Pending[i];

                _frameOperationQueue.Pending.Clear();
                textureUploadOps = _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer;
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            if (_frameOperationQueue.DrainedFrameOpsBuffer.Length != staticCount)
                _frameOperationQueue.DrainedFrameOpsBuffer = new FrameOp[staticCount];

            int staticIndex = 0;
            int uploadIndex = 0;
            for (int i = 0; i < opCount; i++)
            {
                FrameOp op = _frameOperationQueue.Pending[i];
                if (op is TextureUploadFrameOp)
                    _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = op;
                else
                    _frameOperationQueue.DrainedFrameOpsBuffer[staticIndex++] = op;
            }

            _frameOperationQueue.Pending.Clear();
            textureUploadOps = _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer;
            signature = computeSignature ? ComputeFrameOpsSignature(_frameOperationQueue.DrainedFrameOpsBuffer) : 0;
            return _frameOperationQueue.DrainedFrameOpsBuffer;
        }
    }

    internal FrameOp[] DrainTextureUploadFrameOps()
    {
        using (_frameOperationQueue.SyncRoot.EnterScope())
        {
            if (_frameOperationQueue.Pending.Count == 0)
                return Array.Empty<FrameOp>();

            int opCount = _frameOperationQueue.Pending.Count;
            int uploadCount = 0;
            for (int i = 0; i < opCount; i++)
            {
                if (_frameOperationQueue.Pending[i] is TextureUploadFrameOp)
                    uploadCount++;
            }

            if (uploadCount == 0)
                return Array.Empty<FrameOp>();

            if (_frameOperationQueue.DrainedTextureUploadFrameOpsBuffer.Length != uploadCount)
                _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer = new FrameOp[uploadCount];

            int retainedIndex = 0;
            int uploadIndex = 0;
            for (int i = 0; i < opCount; i++)
            {
                FrameOp op = _frameOperationQueue.Pending[i];
                if (op is TextureUploadFrameOp)
                    _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer[uploadIndex++] = op;
                else
                    _frameOperationQueue.Pending[retainedIndex++] = op;
            }

            if (retainedIndex < _frameOperationQueue.Pending.Count)
                _frameOperationQueue.Pending.RemoveRange(retainedIndex, _frameOperationQueue.Pending.Count - retainedIndex);

            return _frameOperationQueue.DrainedTextureUploadFrameOpsBuffer;
        }
    }

    internal FrameOp[] DrainFrameOpsExcludingTextureUploads(out ulong signature, bool computeSignature = true)
    {
        using (_frameOperationQueue.SyncRoot.EnterScope())
        {
            if (_frameOperationQueue.Pending.Count == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            int opCount = _frameOperationQueue.Pending.Count;
            int uploadCount = 0;
            for (int i = 0; i < opCount; i++)
            {
                if (_frameOperationQueue.Pending[i] is TextureUploadFrameOp)
                    uploadCount++;
            }

            if (uploadCount == 0)
            {
                if (_frameOperationQueue.DrainedFrameOpsBuffer.Length != opCount)
                    _frameOperationQueue.DrainedFrameOpsBuffer = new FrameOp[opCount];

                _frameOperationQueue.Pending.CopyTo(_frameOperationQueue.DrainedFrameOpsBuffer);
                _frameOperationQueue.Pending.Clear();
                signature = computeSignature ? ComputeFrameOpsSignature(_frameOperationQueue.DrainedFrameOpsBuffer) : 0;
                return _frameOperationQueue.DrainedFrameOpsBuffer;
            }

            int drainedCount = opCount - uploadCount;
            if (drainedCount == 0)
            {
                signature = 0;
                return Array.Empty<FrameOp>();
            }

            if (_frameOperationQueue.DrainedFrameOpsBuffer.Length != drainedCount)
                _frameOperationQueue.DrainedFrameOpsBuffer = new FrameOp[drainedCount];

            int drainedIndex = 0;
            int retainedIndex = 0;
            for (int i = 0; i < opCount; i++)
            {
                FrameOp op = _frameOperationQueue.Pending[i];
                if (op is TextureUploadFrameOp)
                {
                    _frameOperationQueue.Pending[retainedIndex++] = op;
                }
                else
                {
                    _frameOperationQueue.DrainedFrameOpsBuffer[drainedIndex++] = op;
                }
            }

            if (retainedIndex < _frameOperationQueue.Pending.Count)
                _frameOperationQueue.Pending.RemoveRange(retainedIndex, _frameOperationQueue.Pending.Count - retainedIndex);

            signature = computeSignature ? ComputeFrameOpsSignature(_frameOperationQueue.DrainedFrameOpsBuffer) : 0;
            return _frameOperationQueue.DrainedFrameOpsBuffer;
        }
    }

    private static ulong ComputeFrameOpsSignature(FrameOperationSequence ops)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(ops.Length);

        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            hash.Add(GetFrameOpKindId(op));
            hash.Add(op.PassIndex);
            hash.Add(ResolveCommandChainTargetIdentity(op));
            hash.Add((int)op.Context.ContextKind);
            hash.Add(op.Context.RecordingFingerprint);
            hash.Add(op.Context.PipelineIdentity);
            hash.Add(op.Context.ViewportIdentity);
            hash.Add(op.Context.OutputFrameBufferIdentity);
            hash.Add(op.Context.OutputTargetIdentity);

            switch (op)
            {
                case ClearOp clear:
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
                case MeshDrawOp meshDraw:
                    ref readonly PendingMeshDraw draw = ref meshDraw.DrawRef;
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
                case QueryOp query:
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
                case BlitOp blit:
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
                case IndirectDrawOp indirect:
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
                    hash.Add(indirect.DrawCount);
                    hash.Add(indirect.Stride);
                    hash.Add(indirect.ByteOffset);
                    hash.Add(indirect.CountByteOffset);
                    hash.Add(indirect.UseCount);
                    hash.Add(
                        (int)indirect.SecondaryRecordingContract.Eligibility);
                    break;
                case MeshTaskDispatchIndirectCountOp meshTaskDispatch:
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.IndirectBuffer));
                    hash.Add(ComputeCommandBufferDataBufferSignature(meshTaskDispatch.CountBuffer));
                    hash.Add(meshTaskDispatch.MaxDrawCount);
                    hash.Add(meshTaskDispatch.Stride);
                    hash.Add(meshTaskDispatch.ByteOffset);
                    hash.Add(meshTaskDispatch.CountByteOffset);
                    break;
                case MemoryBarrierOp barrier:
                    hash.Add((int)barrier.Mask);
                    break;
                case PublishFramebufferForSamplingOp publish:
                    hash.Add(publish.FrameBuffer.GetHashCode());
                    break;
                case DlssUpscaleOp dlss:
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
                case DlssFrameGenerationOp dlssFrameGeneration:
                    hash.Add(dlssFrameGeneration.Session.GetHashCode());
                    hash.Add(dlssFrameGeneration.Depth.Image.Handle);
                    hash.Add(dlssFrameGeneration.Motion.Image.Handle);
                    hash.Add(dlssFrameGeneration.HudlessColor.Image.Handle);
                    hash.Add(dlssFrameGeneration.Parameters.InputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.InputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.OutputWidth);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHeight);
                    hash.Add(dlssFrameGeneration.Parameters.FrameIndex);
                    hash.Add(dlssFrameGeneration.Parameters.ResetHistory);
                    hash.Add(dlssFrameGeneration.Parameters.OutputHdr);
                    break;
                case TransformFeedbackOp transformFeedback:
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
                case ComputeDispatchOp compute:
                    hash.Add(compute.Program.GetHashCode());
                    hash.Add(compute.GroupsX);
                    hash.Add(compute.GroupsY);
                    hash.Add(compute.GroupsZ);
                    HashProgramBindingLayoutSnapshot(ref hash, compute.Snapshot);
                    break;
                case ComputeDispatchIndirectOp computeIndirect:
                    hash.Add(computeIndirect.Program.GetHashCode());
                    hash.Add(computeIndirect.ArgumentBuffer.Handle);
                    hash.Add(computeIndirect.ArgumentOffset);
                    HashProgramBindingLayoutSnapshot(ref hash, computeIndirect.Snapshot);
                    break;
                case BufferCopyOp copy:
                    hash.Add(copy.SourceBuffer.Handle);
                    hash.Add(copy.SourceOffset);
                    hash.Add(copy.DestinationBuffer.Handle);
                    hash.Add(copy.DestinationOffset);
                    hash.Add(copy.ByteCount);
                    break;
                case SubmissionMarkerOp:
                    // The fence object is CPU-side submission state and is rebound
                    // whenever a cached primary is reused. Marker position remains
                    // part of the structural signature because recording it closes
                    // the active render pass.
                    break;
                case TextureUploadFrameOp upload:
                    hash.Add(upload.Upload.PublicationToken);
                    hash.Add(upload.Upload.Request.StreamingGeneration);
                    hash.Add(upload.Upload.Image.Handle);
                    hash.Add(upload.Upload.ImageView.Handle);
                    hash.Add(upload.Upload.Sampler.Handle);
                    hash.Add(upload.Upload.Extent.Width);
                    hash.Add(upload.Upload.Extent.Height);
                    hash.Add(upload.Upload.MipLevels);
                    hash.Add((ulong)Math.Max(upload.Upload.CommittedBytes, 0L));
                    hash.Add(upload.Upload.StagingResources.Length);
                    break;
            }
        }

        return hash.ToHash();
    }

    private static int GetFrameOpKindId(FrameOp op)
        => op switch
        {
            ClearOp => FrameOpKindClear,
            MeshDrawOp => FrameOpKindMeshDraw,
            QueryOp => FrameOpKindQuery,
            BlitOp => FrameOpKindBlit,
            IndirectDrawOp => FrameOpKindIndirectDraw,
            MeshTaskDispatchIndirectCountOp => FrameOpKindMeshTaskDispatchIndirectCount,
            MemoryBarrierOp => FrameOpKindMemoryBarrier,
            PublishFramebufferForSamplingOp => FrameOpKindPublishFramebufferForSampling,
            DlssUpscaleOp => FrameOpKindDlssUpscale,
            DlssFrameGenerationOp => FrameOpKindDlssFrameGeneration,
            TransformFeedbackOp => FrameOpKindTransformFeedback,
            ComputeDispatchOp => FrameOpKindComputeDispatch,
            ComputeDispatchIndirectOp => FrameOpKindComputeDispatchIndirect,
            BufferCopyOp => FrameOpKindBufferCopy,
            SubmissionMarkerOp => FrameOpKindSubmissionMarker,
            TextureUploadFrameOp => FrameOpKindTextureUpload,
            _ => FrameOpKindUnknown
        };

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
        if (snapshot.HasPublishedBindingLayoutSignatures)
        {
            hash.Add(snapshot.SamplerUnitBindingLayoutSignature);
            hash.Add(snapshot.SamplerNameBindingLayoutSignature);
            hash.Add(snapshot.ImageBindingLayoutSignature);
            hash.Add(snapshot.BufferBindingLayoutSignature);
            return;
        }

        hash.Add(HashSamplerUnitBindingLayout(snapshot.Samplers, snapshot.SamplerNamesByUnit));
        hash.Add(HashSamplerNameBindingLayout(snapshot.SamplersByName));
        hash.Add(HashImageBindingLayout(snapshot.Images));
        hash.Add(HashBufferBindingLayout(snapshot.Buffers));
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
