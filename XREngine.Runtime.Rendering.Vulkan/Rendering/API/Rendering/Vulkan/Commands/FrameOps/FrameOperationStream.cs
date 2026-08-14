namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Canonical post-ordering operation stream. Headers and per-kind dense
/// payload records are the complete sealed representation: no <see cref="FrameOp"/>
/// instance survives this lowering boundary.
/// </summary>
internal sealed class FrameOperationStream
{
    private const int KindCount = (int)EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership + 1;
    private readonly FrameOperationPayloadStore _payloads;
    private FrameOperationHeader[] _headers = new FrameOperationHeader[64];
    private FrameOperationHeader[] _headerOrderScratch = new FrameOperationHeader[64];
    private FrameOpContext[] _contexts = new FrameOpContext[64];
    private FrameOpResourceUse[] _resourceUses = new FrameOpResourceUse[256];
    private XRFrameBuffer?[] _targets = new XRFrameBuffer?[64];
    private int _count;
    private int _resourceUseCount;

    internal static FrameOperationStream Empty { get; } = new();
    internal int Count => _count;

    internal FrameOperationStream()
        : this(new FrameOperationPayloadStore()) { }

    private FrameOperationStream(FrameOperationPayloadStore payloads)
        => _payloads = payloads;

    internal void Reset()
    {
        if (_count > 0)
        {
            Array.Clear(_headers, 0, _count);
            Array.Clear(_contexts, 0, _count);
            Array.Clear(_targets, 0, _count);
        }
        _count = 0;
        _resourceUseCount = 0;
    }

    internal void CopySourceOrderTo(Span<int> destination)
    {
        if (destination.Length < _count)
            throw new ArgumentException("The destination is smaller than the operation stream.", nameof(destination));
        for (int index = 0; index < _count; index++) destination[index] = _headers[index].OriginalIndex;
    }

    /// <summary>
    /// Consumes producer objects in source order before any sorting, dependency
    /// planning, or worker scheduling can observe the frame. Subsequent order
    /// changes move numeric headers only; dense payload records are stable.
    /// </summary>
    internal void Lower(FrameOperationIngress source)
    {
        Reset();
        EnsureCapacity(source.Count);
        Span<int> payloadCounts = stackalloc int[KindCount];
        int resourceUseCount = 0;
        for (int index = 0; index < source.Count; index++)
        {
            FrameOp operation = source.GetAuthoringOperation(index);
            int kind = (int)operation.Kind;
            if ((uint)kind >= KindCount) throw new InvalidOperationException("Frame operation has an unsupported opcode.");
            payloadCounts[kind]++;
            resourceUseCount += operation.ResourceUsesReference.Count;
        }
        EnsureResourceUseCapacity(resourceUseCount);
        for (int kind = 0; kind < KindCount; kind++) _payloads.EnsureCapacity((EVulkanPrimaryPlanNodeKind)kind, payloadCounts[kind]);

        payloadCounts.Clear();
        for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            FrameOp operation = source.GetAuthoringOperation(sourceIndex);
            EVulkanPrimaryPlanNodeKind kind = operation.Kind;
            int payloadIndex = payloadCounts[(int)kind]++;
            StorePayload(kind, payloadIndex, operation);
            bool preserveSubmissionOrder = operation is MeshDrawOp meshDraw && meshDraw.PreserveSubmissionOrder;
            ref readonly FrameOpResourceUseList operationResourceUses =
                ref operation.ResourceUsesReference;
            int resourceUseOffset = _resourceUseCount;
            int operationResourceUseCount = operationResourceUses.Count;
            operationResourceUses.CopyTo(
                _resourceUses.AsSpan(
                    resourceUseOffset,
                    operationResourceUseCount));
            _resourceUseCount += operationResourceUseCount;
            _headers[sourceIndex] = new FrameOperationHeader(kind, payloadIndex, operation.PassIndex, operation.ContextReference.OutputTargetIdentity, sourceIndex, resourceUseOffset, operationResourceUseCount, sourceIndex, operation.RequiresPrimaryRecordingContext, preserveSubmissionOrder);
            _contexts[sourceIndex] = operation.ContextReference;
            _targets[sourceIndex] = operation.Target;
        }
        _count = source.Count;
        source.Clear();
    }

    /// <summary>Applies a compiled order to numeric headers without rebuilding payloads.</summary>
    internal void Reorder(ReadOnlySpan<int> order)
    {
        if (order.Length != _count)
            throw new ArgumentException("The order must contain every operation exactly once.", nameof(order));
        if (_headerOrderScratch.Length < _count)
            Array.Resize(ref _headerOrderScratch, Math.Max(_count, _headerOrderScratch.Length * 2));

        for (int index = 0; index < _count; index++)
        {
            int sourceIndex = order[index];
            if ((uint)sourceIndex >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(order), "An operation index is outside the stream.");
            _headerOrderScratch[index] = _headers[sourceIndex];
        }
        _headerOrderScratch.AsSpan(0, _count).CopyTo(_headers);
    }

    /// <summary>
    /// Retains a caller-selected subset of headers while preserving the dense,
    /// immutable payload columns. Used by output admission before the frame plan
    /// is sealed so deferred output work cannot reach native recording.
    /// </summary>
    internal void Retain(ReadOnlySpan<int> retainedIndices)
    {
        if (retainedIndices.Length > _count)
            throw new ArgumentException("The retained operation count exceeds the stream.", nameof(retainedIndices));
        if (_headerOrderScratch.Length < retainedIndices.Length)
            Array.Resize(
                ref _headerOrderScratch,
                Math.Max(retainedIndices.Length, _headerOrderScratch.Length * 2));

        for (int index = 0; index < retainedIndices.Length; index++)
        {
            int sourceIndex = retainedIndices[index];
            if ((uint)sourceIndex >= (uint)_count)
                throw new ArgumentOutOfRangeException(
                    nameof(retainedIndices),
                    "A retained operation index is outside the stream.");
            _headerOrderScratch[index] = _headers[sourceIndex];
        }

        _headerOrderScratch.AsSpan(0, retainedIndices.Length).CopyTo(_headers);
        if (retainedIndices.Length < _count)
            Array.Clear(_headers, retainedIndices.Length, _count - retainedIndices.Length);
        _count = retainedIndices.Length;
    }

    /// <summary>
    /// Copies a view's numeric headers while sharing the immutable dense payload
    /// columns. Used only after a frame plan is sealed for per-eye recording.
    /// </summary>
    internal FrameOperationStream CreateLogicalViewSlice(ulong logicalViewId)
    {
        if (logicalViewId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(logicalViewId));

        int matchCount = 0;
        for (int index = 0; index < _count; index++)
            if (GetContext(index).LogicalViewId == logicalViewId)
                matchCount++;

        FrameOperationStream slice = new(_payloads);
        slice.EnsureCapacity(matchCount);
        int matchingResourceUseCount = 0;
        for (int sourceIndex = 0; sourceIndex < _count; sourceIndex++)
        {
            ref readonly FrameOperationHeader header = ref _headers[sourceIndex];
            if (_contexts[header.ContextIndex].LogicalViewId == logicalViewId)
                matchingResourceUseCount += header.ResourceUseCount;
        }
        slice.EnsureResourceUseCapacity(matchingResourceUseCount);
        for (int sourceIndex = 0, destinationIndex = 0; sourceIndex < _count; sourceIndex++)
        {
            ref readonly FrameOperationHeader header = ref _headers[sourceIndex];
            if (_contexts[header.ContextIndex].LogicalViewId != logicalViewId)
                continue;

            int destinationResourceUseOffset = slice._resourceUseCount;
            _resourceUses.AsSpan(
                header.ResourceUseOffset,
                header.ResourceUseCount).CopyTo(
                    slice._resourceUses.AsSpan(
                        destinationResourceUseOffset,
                        header.ResourceUseCount));
            slice._resourceUseCount += header.ResourceUseCount;
            slice._headers[destinationIndex] = header with
            {
                ContextIndex = destinationIndex,
                ResourceUseOffset = destinationResourceUseOffset,
            };
            slice._contexts[destinationIndex] = _contexts[header.ContextIndex];
            slice._targets[destinationIndex] = _targets[header.ContextIndex];
            destinationIndex++;
        }
        slice._count = matchCount;
        return slice;
    }

    internal ref readonly FrameOperationHeader GetHeader(int index)
    {
        if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        return ref _headers[index];
    }
    internal ref readonly FrameOpContext GetContext(int index) => ref _contexts[GetHeader(index).ContextIndex];
    internal ReadOnlySpan<FrameOpResourceUse> GetResourceUses(int index)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        return _resourceUses.AsSpan(
            header.ResourceUseOffset,
            header.ResourceUseCount);
    }
    internal XRFrameBuffer? GetTarget(int index) => _targets[GetHeader(index).ContextIndex];

    internal bool TryGetMeshDraw(int index, out MeshDrawPayload payload)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        if (header.OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw) { payload = default; return false; }
        payload = _payloads.MeshDraws[header.PayloadIndex]; return true;
    }
    internal bool TryGetIndirectDraw(int index, out IndirectDrawPayload payload)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        if (header.OpCode != EVulkanPrimaryPlanNodeKind.IndirectDraw) { payload = default; return false; }
        payload = _payloads.IndirectDraws[header.PayloadIndex]; return true;
    }
    internal bool TryGetComputeDispatch(int index, out ComputeDispatchPayload payload)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        if (header.OpCode != EVulkanPrimaryPlanNodeKind.ComputeDispatch) { payload = default; return false; }
        payload = _payloads.ComputeDispatches[header.PayloadIndex]; return true;
    }

    internal ref readonly TextureUploadPayload GetTextureUpload(int index) => ref _payloads.TextureUploads[RequireKind(index, EVulkanPrimaryPlanNodeKind.TextureUpload).PayloadIndex];
    internal ref readonly BlitPayload GetBlit(int index) => ref _payloads.Blits[RequireKind(index, EVulkanPrimaryPlanNodeKind.Blit).PayloadIndex];
    internal ref readonly ClearPayload GetClear(int index) => ref _payloads.Clears[RequireKind(index, EVulkanPrimaryPlanNodeKind.Clear).PayloadIndex];
    internal ref readonly TransformFeedbackPayload GetTransformFeedback(int index) => ref _payloads.TransformFeedbacks[RequireKind(index, EVulkanPrimaryPlanNodeKind.TransformFeedback).PayloadIndex];
    internal ref readonly QueryPayload GetQuery(int index) => ref _payloads.Queries[RequireKind(index, EVulkanPrimaryPlanNodeKind.Query).PayloadIndex];
    internal ref readonly MeshDrawPayload GetMeshDraw(int index) => ref _payloads.MeshDraws[RequireKind(index, EVulkanPrimaryPlanNodeKind.MeshDraw).PayloadIndex];
    internal ref readonly IndirectDrawPayload GetIndirectDraw(int index) => ref _payloads.IndirectDraws[RequireKind(index, EVulkanPrimaryPlanNodeKind.IndirectDraw).PayloadIndex];
    internal ref readonly MeshTaskDispatchIndirectCountPayload GetMeshTask(int index) => ref _payloads.MeshTasks[RequireKind(index, EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount).PayloadIndex];
    internal ref readonly ComputeDispatchPayload GetComputeDispatch(int index) => ref _payloads.ComputeDispatches[RequireKind(index, EVulkanPrimaryPlanNodeKind.ComputeDispatch).PayloadIndex];
    internal ref readonly ComputeDispatchIndirectPayload GetComputeDispatchIndirect(int index) => ref _payloads.ComputeDispatchIndirects[RequireKind(index, EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect).PayloadIndex];
    internal ref readonly BufferCopyPayload GetBufferCopy(int index) => ref _payloads.BufferCopies[RequireKind(index, EVulkanPrimaryPlanNodeKind.BufferCopy).PayloadIndex];
    internal ref readonly SubmissionMarkerPayload GetSubmissionMarker(int index) => ref _payloads.SubmissionMarkers[RequireKind(index, EVulkanPrimaryPlanNodeKind.SubmissionMarker).PayloadIndex];
    internal ref readonly MemoryBarrierPayload GetMemoryBarrier(int index) => ref _payloads.MemoryBarriers[RequireKind(index, EVulkanPrimaryPlanNodeKind.MemoryBarrier).PayloadIndex];
    internal ref readonly PublishFramebufferPayload GetPublishedFramebuffer(int index) => ref _payloads.PublishedFramebuffers[RequireKind(index, EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling).PayloadIndex];
    internal ref readonly DlssUpscalePayload GetDlssUpscale(int index) => ref _payloads.DlssUpscales[RequireKind(index, EVulkanPrimaryPlanNodeKind.DlssUpscale).PayloadIndex];
    internal ref readonly DlssFrameGenerationPayload GetDlssFrameGeneration(int index) => ref _payloads.DlssFrameGenerations[RequireKind(index, EVulkanPrimaryPlanNodeKind.DlssFrameGeneration).PayloadIndex];

    private ref readonly FrameOperationHeader RequireKind(int index, EVulkanPrimaryPlanNodeKind kind)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        if (header.OpCode != kind) throw new InvalidOperationException($"Operation {index} is {header.OpCode}, not {kind}.");
        return ref header;
    }

    private void StorePayload(EVulkanPrimaryPlanNodeKind kind, int i, FrameOp op)
    {
        switch (kind)
        {
            case EVulkanPrimaryPlanNodeKind.TextureUpload: _payloads.TextureUploads[i] = new(((TextureUploadFrameOp)op).Upload); break;
            case EVulkanPrimaryPlanNodeKind.Blit: { var p=(BlitOp)op; _payloads.Blits[i]=new(p.InFbo,p.OutFbo,p.InX,p.InY,p.InW,p.InH,p.OutX,p.OutY,p.OutW,p.OutH,p.ReadBufferMode,p.ColorBit,p.DepthBit,p.StencilBit,p.LinearFilter); break; }
            case EVulkanPrimaryPlanNodeKind.Clear: { var p=(ClearOp)op; _payloads.Clears[i]=new(p.ClearColor,p.ClearDepth,p.ClearStencil,p.Color,p.Depth,p.Stencil,p.Rect); break; }
            case EVulkanPrimaryPlanNodeKind.TransformFeedback: { var p=(TransformFeedbackOp)op; _payloads.TransformFeedbacks[i]=new(p.TransformFeedback,p.Operation,p.CounterBuffer,p.FeedbackBufferOffset,p.FeedbackBufferSize,p.CounterBufferOffset,p.CounterOffset,p.VertexStride,p.InstanceCount,p.FirstInstance); break; }
            case EVulkanPrimaryPlanNodeKind.Query: { var p=(QueryOp)op; _payloads.Queries[i]=new(p.Query,p.Descriptor,p.Operation,p.TimestampStage,p.PointIndex,p.SourceHandles,p.ResultDestination,p.ResultDestinationOffset,p.ResultStride,p.IncludeAvailability); break; }
            case EVulkanPrimaryPlanNodeKind.MeshDraw: _payloads.MeshDraws[i]=new(((MeshDrawOp)op).Draw.CreateSealedCopy()); break;
            case EVulkanPrimaryPlanNodeKind.IndirectDraw: { var p=(IndirectDrawOp)op; _payloads.IndirectDraws[i]=new(p.IndirectBuffer,p.ParameterBuffer,p.MeshRenderer,p.Draw.CreateSealedCopy(),p.DrawCount,p.Stride,p.ByteOffset,p.CountByteOffset,p.UseCount,p.BindlessMaterialTextures,p.SecondaryRecordingContract); break; }
            case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount: { var p=(MeshTaskDispatchIndirectCountOp)op; _payloads.MeshTasks[i]=new(p.IndirectBuffer,p.CountBuffer,p.MaxDrawCount,p.Stride,p.ByteOffset,p.CountByteOffset,p.BindlessMaterialTextures); break; }
            case EVulkanPrimaryPlanNodeKind.ComputeDispatch: { var p=(ComputeDispatchOp)op; _payloads.ComputeDispatches[i]=new(p.Program,p.GroupsX,p.GroupsY,p.GroupsZ,p.Snapshot.CreateSealedCopy()); break; }
            case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect: { var p=(ComputeDispatchIndirectOp)op; _payloads.ComputeDispatchIndirects[i]=new(p.Program,p.Snapshot.CreateSealedCopy(),p.ArgumentOwner,p.ArgumentBuffer,p.ArgumentOffset,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.BufferCopy: { var p=(BufferCopyOp)op; _payloads.BufferCopies[i]=new(p.SourceOwner,p.SourceBuffer,p.SourceOffset,p.DestinationOwner,p.DestinationBuffer,p.DestinationOffset,p.ByteCount,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.SubmissionMarker: { var p=(SubmissionMarkerOp)op; _payloads.SubmissionMarkers[i]=new(p.Fence,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.MemoryBarrier: _payloads.MemoryBarriers[i]=new(((MemoryBarrierOp)op).Mask); break;
            case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling: _payloads.PublishedFramebuffers[i]=new(((PublishFramebufferForSamplingOp)op).FrameBuffer); break;
            case EVulkanPrimaryPlanNodeKind.DlssUpscale: { var p=(DlssUpscaleOp)op; _payloads.DlssUpscales[i]=new(p.Session,p.SourceColor,p.Depth,p.Motion,p.OutputColor,p.Exposure,p.Parameters); break; }
            case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration: { var p=(DlssFrameGenerationOp)op; _payloads.DlssFrameGenerations[i]=new(p.Session,p.Depth,p.Motion,p.HudlessColor,p.Parameters,p.UiColorAndAlpha); break; }
            default: throw new InvalidOperationException($"No payload writer exists for {kind}.");
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_headers.Length < required) Array.Resize(ref _headers, Math.Max(required, _headers.Length * 2));
        if (_contexts.Length < required) Array.Resize(ref _contexts, Math.Max(required, _contexts.Length * 2));
        if (_targets.Length < required) Array.Resize(ref _targets, Math.Max(required, _targets.Length * 2));
    }

    private void EnsureResourceUseCapacity(int required)
    {
        if (_resourceUses.Length < required)
            Array.Resize(
                ref _resourceUses,
                Math.Max(required, _resourceUses.Length * 2));
    }
}
