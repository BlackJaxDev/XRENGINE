using Silk.NET.Vulkan;

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
    private readonly bool _fixedCapacity;
    private readonly EVulkanAcceptedFrameLane _lane;
    private FrameOperationHeader[] _headers;
    private FrameOperationHeader[] _headerOrderScratch;
    private FrameOpContext[] _contexts;
    private FrameOpResourceUse[] _resourceUses;
    private XRFrameBuffer?[] _targets;
    private int _count;
    private int _resourceUseCount;
    private int _meshPayloadCount;

    internal static FrameOperationStream Empty { get; } = new();
    internal int Count => _count;
    internal int Capacity => _headers.Length;
    internal int ResourceUseCapacity => _resourceUses.Length;

    internal FrameOperationStream()
        : this(new FrameOperationPayloadStore())
    {
    }

    private FrameOperationStream(FrameOperationPayloadStore payloads)
    {
        _payloads = payloads;
        _fixedCapacity = false;
        _lane = EVulkanAcceptedFrameLane.MainScene;
        _headers = new FrameOperationHeader[64];
        _headerOrderScratch = new FrameOperationHeader[64];
        _contexts = new FrameOpContext[64];
        _resourceUses = new FrameOpResourceUse[256];
        _targets = new XRFrameBuffer?[64];
    }

    /// <summary>
    /// Creates fixed frame-plan-owned header storage that shares this stream's
    /// immutable payload columns. Logical OpenXR views only remap headers and
    /// resource-use offsets, so duplicating payload columns would both waste
    /// memory and permit them to diverge from the sealed source stream.
    /// </summary>
    internal FrameOperationStream CreateLogicalViewStorage(
        int operationCapacity,
        int resourceUseCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operationCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceUseCapacity);
        return new FrameOperationStream(
            _payloads,
            operationCapacity,
            resourceUseCapacity,
            _lane);
    }

    private FrameOperationStream(
        FrameOperationPayloadStore payloads,
        int operationCapacity,
        int resourceUseCapacity,
        EVulkanAcceptedFrameLane lane)
    {
        _payloads = payloads;
        _fixedCapacity = true;
        _lane = lane;
        _headers = new FrameOperationHeader[operationCapacity];
        _headerOrderScratch = new FrameOperationHeader[operationCapacity];
        _contexts = new FrameOpContext[operationCapacity];
        _resourceUses = new FrameOpResourceUse[resourceUseCapacity];
        _targets = new XRFrameBuffer?[operationCapacity];
    }

    /// <summary>
    /// Creates frame-slot storage whose complete budget is allocated before a
    /// foreground frame can be accepted. Capacity failure is explicit rather
    /// than an allocation in lowering or recording.
    /// </summary>
    internal FrameOperationStream(
        int operationCapacity,
        int resourceUseCapacity,
        int generalPayloadCapacity,
        int meshPayloadCapacity,
        int texturePayloadCapacity,
        EVulkanAcceptedFrameLane lane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operationCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(resourceUseCapacity);
        _payloads = new FrameOperationPayloadStore(
            generalPayloadCapacity,
            meshPayloadCapacity,
            texturePayloadCapacity,
            fixedCapacity: true,
            lane);
        _fixedCapacity = true;
        _lane = lane;
        _headers = new FrameOperationHeader[operationCapacity];
        _headerOrderScratch = new FrameOperationHeader[operationCapacity];
        _contexts = new FrameOpContext[operationCapacity];
        _resourceUses = new FrameOpResourceUse[resourceUseCapacity];
        _targets = new XRFrameBuffer?[operationCapacity];
    }

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
        _meshPayloadCount = 0;
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

        int meshPayloadCount = payloadCounts[(int)EVulkanPrimaryPlanNodeKind.MeshDraw];
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
        _meshPayloadCount = meshPayloadCount;
        source.Clear();
    }

    /// <summary>
    /// Appends one prepared mesh partition without creating authoring
    /// <see cref="FrameOp"/> objects. Static and dynamic UI entries retain their
    /// independent streams while preserving cohort order within each partition.
    /// </summary>
    internal void AppendPreparedMeshIngress(
        VulkanPreparedMeshIngress ingress,
        bool dynamicUi)
    {
        if (ingress.Count == 0)
            return;

        int appendCount = 0;
        int appendResourceUseCount = 0;
        for (int index = 0; index < ingress.Count; index++)
        {
            ref readonly VulkanPreparedMeshIngressEntry entry =
                ref ingress.GetEntry(index);
            if (entry.IsDynamicUi != dynamicUi)
                continue;
            appendCount++;
            appendResourceUseCount += entry.ResourceUseCount;
        }
        if (appendCount == 0)
            return;

        int operationStart = _count;
        int meshPayloadStart = _meshPayloadCount;
        EnsureCapacity(operationStart + appendCount);
        _payloads.EnsureCapacity(
            EVulkanPrimaryPlanNodeKind.MeshDraw,
            meshPayloadStart + appendCount);
        EnsureResourceUseCapacity(_resourceUseCount + appendResourceUseCount);
        int appendIndex = 0;
        for (int index = 0; index < ingress.Count; index++)
        {
            ref readonly VulkanPreparedMeshIngressEntry entry =
                ref ingress.GetEntry(index);
            if (entry.IsDynamicUi != dynamicUi)
                continue;

            int operationIndex = operationStart + appendIndex;
            int payloadIndex = meshPayloadStart + appendIndex;
            _payloads.MeshDraws[payloadIndex] = new(
                entry.Draw.CreateSealedCopy());
            int resourceOffset = _resourceUseCount;
            ingress.GetResourceUses(in entry).CopyTo(_resourceUses.AsSpan(resourceOffset));
            _resourceUseCount += entry.ResourceUseCount;
            _contexts[operationIndex] = entry.Context;
            _targets[operationIndex] = entry.Target;
            _headers[operationIndex] = new(
                EVulkanPrimaryPlanNodeKind.MeshDraw,
                payloadIndex,
                entry.PassIndex,
                entry.Context.OutputTargetIdentity,
                operationIndex,
                resourceOffset,
                entry.ResourceUseCount,
                operationIndex,
                true,
                entry.PreserveSubmissionOrder);
            appendIndex++;
        }
        _count += appendCount;
        _meshPayloadCount += appendCount;
    }

    /// <summary>Applies a compiled order to numeric headers without rebuilding payloads.</summary>
    internal void Reorder(ReadOnlySpan<int> order)
    {
        if (order.Length != _count)
            throw new ArgumentException("The order must contain every operation exactly once.", nameof(order));
        EnsureHeaderOrderCapacity(_count);

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
        EnsureHeaderOrderCapacity(retainedIndices.Length);

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
    /// Copies a view's numeric headers into preallocated frame-plan storage
    /// while sharing the immutable dense payload columns. This is the sole
    /// logical-view materialization boundary and cannot allocate during eye
    /// recording.
    /// </summary>
    internal void CopyLogicalViewSliceTo(
        ulong logicalViewId,
        FrameOperationStream destination)
    {
        if (logicalViewId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(logicalViewId));
        ArgumentNullException.ThrowIfNull(destination);
        if (!ReferenceEquals(_payloads, destination._payloads))
            throw new InvalidOperationException("Logical-view storage must share the sealed stream payload store.");

        int matchCount = 0;
        for (int index = 0; index < _count; index++)
            if (GetContext(index).LogicalViewId == logicalViewId)
                matchCount++;

        int matchingResourceUseCount = 0;
        for (int sourceIndex = 0; sourceIndex < _count; sourceIndex++)
        {
            ref readonly FrameOperationHeader header = ref _headers[sourceIndex];
            if (_contexts[header.ContextIndex].LogicalViewId == logicalViewId)
                matchingResourceUseCount += header.ResourceUseCount;
        }
        destination.Reset();
        destination.EnsureCapacity(matchCount);
        destination.EnsureResourceUseCapacity(matchingResourceUseCount);
        for (int sourceIndex = 0, destinationIndex = 0; sourceIndex < _count; sourceIndex++)
        {
            ref readonly FrameOperationHeader header = ref _headers[sourceIndex];
            if (_contexts[header.ContextIndex].LogicalViewId != logicalViewId)
                continue;

            int destinationResourceUseOffset = destination._resourceUseCount;
            _resourceUses.AsSpan(
                header.ResourceUseOffset,
                header.ResourceUseCount).CopyTo(
                    destination._resourceUses.AsSpan(
                        destinationResourceUseOffset,
                        header.ResourceUseCount));
            destination._resourceUseCount += header.ResourceUseCount;
            destination._headers[destinationIndex] = header with
            {
                ContextIndex = destinationIndex,
                ResourceUseOffset = destinationResourceUseOffset,
            };
            destination._contexts[destinationIndex] = _contexts[header.ContextIndex];
            destination._targets[destinationIndex] = _targets[header.ContextIndex];
            destinationIndex++;
        }
        destination._count = matchCount;
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

    /// <summary>
    /// Replaces the concrete native pipeline of a mesh-task payload at the one
    /// legal boundary between plan sealing and primary command recording.
    /// Every producer-owned value is checked so this cannot retarget an
    /// operation or mix bindings from another sealed operation.
    /// </summary>
    internal bool TryAssociateAdmittedMeshTaskPipeline(
        int index,
        VkRenderProgram program,
        ulong programLinkGeneration,
        ComputeDispatchSnapshot programBindingSnapshot,
        in VulkanMeshProducerSnapshot producerSnapshot,
        Pipeline pipeline)
    {
        if (pipeline.Handle == 0 ||
            (uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount)
        {
            return false;
        }

        ref readonly FrameOperationHeader header = ref _headers[index];
        MeshTaskDispatchIndirectCountPayload payload =
            _payloads.MeshTasks[header.PayloadIndex];
        if (!ReferenceEquals(payload.Program, program) ||
            payload.ProgramLinkGeneration != programLinkGeneration ||
            !ReferenceEquals(payload.ProgramBindingSnapshot, programBindingSnapshot) ||
            !payload.ProducerSnapshot.Equals(producerSnapshot))
        {
            return false;
        }

        _payloads.MeshTasks[header.PayloadIndex] = payload with
        {
            Pipeline = pipeline,
        };
        return true;
    }
    internal ref readonly ComputeDispatchPayload GetComputeDispatch(int index) => ref _payloads.ComputeDispatches[RequireKind(index, EVulkanPrimaryPlanNodeKind.ComputeDispatch).PayloadIndex];
    internal ref readonly ComputeDispatchIndirectPayload GetComputeDispatchIndirect(int index) => ref _payloads.ComputeDispatchIndirects[RequireKind(index, EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect).PayloadIndex];
    internal ref readonly BufferCopyPayload GetBufferCopy(int index) => ref _payloads.BufferCopies[RequireKind(index, EVulkanPrimaryPlanNodeKind.BufferCopy).PayloadIndex];
    internal ref readonly SubmissionMarkerPayload GetSubmissionMarker(int index) => ref _payloads.SubmissionMarkers[RequireKind(index, EVulkanPrimaryPlanNodeKind.SubmissionMarker).PayloadIndex];
    internal ref readonly MemoryBarrierPayload GetMemoryBarrier(int index) => ref _payloads.MemoryBarriers[RequireKind(index, EVulkanPrimaryPlanNodeKind.MemoryBarrier).PayloadIndex];
    internal ref readonly PublishFramebufferPayload GetPublishedFramebuffer(int index) => ref _payloads.PublishedFramebuffers[RequireKind(index, EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling).PayloadIndex];
    internal ref readonly DlssUpscalePayload GetDlssUpscale(int index) => ref _payloads.DlssUpscales[RequireKind(index, EVulkanPrimaryPlanNodeKind.DlssUpscale).PayloadIndex];
    internal ref readonly DlssFrameGenerationPayload GetDlssFrameGeneration(int index) => ref _payloads.DlssFrameGenerations[RequireKind(index, EVulkanPrimaryPlanNodeKind.DlssFrameGeneration).PayloadIndex];
    internal ref readonly VulkanAdvancedVisibilityOperationPayload GetAdvancedVisibility(int index) => ref _payloads.AdvancedVisibilities[RequireKind(index, EVulkanPrimaryPlanNodeKind.AdvancedVisibility).PayloadIndex];
    internal VulkanAdvancedVisibilityLateClosureStorage
        GetAdvancedVisibilityLateClosureStorage(int index)
        => _payloads.AdvancedVisibilityLateClosures[
            RequireKind(
                index,
                EVulkanPrimaryPlanNodeKind.AdvancedVisibility).PayloadIndex];

    /// <summary>
    /// Publishes the one admissible native set-1 allocation for a sealed
    /// visibility operation. This is deliberately the only mutation allowed
    /// after lowering: it cannot replace the authoring request or retarget the
    /// logical attachment identities held by the plan.
    /// </summary>
    internal bool TryAssociateAdvancedVisibilityState(
        int index,
        in VulkanAdvancedVisibilityStageRequest request,
        in VulkanAdvancedVisibilityResourceState state,
        VkRenderProgram earlyVisibilityProgram,
        VkRenderProgram buildIndirectProgram)
    {
        if (!state.IsValid || (uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
            return false;

        ref readonly FrameOperationHeader header = ref _headers[index];
        VulkanAdvancedVisibilityOperationPayload payload = _payloads.AdvancedVisibilities[header.PayloadIndex];
        if (!payload.Request.Equals(request) ||
            (payload.SceneState.IsValid &&
             (payload.SceneState.FrameSlot != state.FrameSlot ||
              payload.SceneState.FrameGeneration != state.FrameGeneration)))
            return false;

        Pipeline earlyPipeline = earlyVisibilityProgram.GetOrCreateComputePipeline();
        Pipeline indirectPipeline = buildIndirectProgram.GetOrCreateComputePipeline();
        if (earlyPipeline.Handle == 0 || indirectPipeline.Handle == 0 ||
            earlyVisibilityProgram.PipelineLayout.Handle == 0 ||
            buildIndirectProgram.PipelineLayout.Handle == 0)
        {
            return false;
        }
        _payloads.AdvancedVisibilities[header.PayloadIndex] = payload with
        {
            State = state,
            EarlyVisibilityProgram = earlyVisibilityProgram,
            EarlyVisibilityPipeline = earlyPipeline,
            EarlyVisibilityLinkGeneration = earlyVisibilityProgram.LinkGeneration,
            BuildIndirectProgram = buildIndirectProgram,
            BuildIndirectPipeline = indirectPipeline,
            BuildIndirectLinkGeneration = buildIndirectProgram.LinkGeneration,
        };
        return true;
    }

    internal bool TryAssociateAdvancedVisibilityPublication(
        int index,
        in VulkanAdvancedScenePublicationState sceneState)
    {
        if (!sceneState.IsValid || (uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
            return false;

        ref readonly FrameOperationHeader header = ref _headers[index];
        VulkanAdvancedVisibilityOperationPayload payload =
            _payloads.AdvancedVisibilities[header.PayloadIndex];
        if (payload.SceneState.IsValid && payload.SceneState != sceneState)
            return false;

        _payloads.AdvancedVisibilities[header.PayloadIndex] = payload with
        {
            SceneState = sceneState,
        };
        return true;
    }

    internal bool TryAssociateAdvancedVisibilityTarget(
        int index,
        in VulkanAdvancedVisibilityStageRequest request,
        in VulkanAdvancedVisibilityTargetClosure closure)
    {
        if (!closure.IsValid || (uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
            return false;

        ref readonly FrameOperationHeader header = ref _headers[index];
        VulkanAdvancedVisibilityOperationPayload payload =
            _payloads.AdvancedVisibilities[header.PayloadIndex];
        if (!payload.Request.Equals(request) ||
            !ReferenceEquals(request.Target, closure.Target))
            return false;

        _payloads.AdvancedVisibilities[header.PayloadIndex] = payload with
        {
            TargetClosure = closure,
        };
        return true;
    }

    internal bool TryAssociateAdvancedVisibilityLateClosure(
        int index,
        in VulkanAdvancedVisibilityStageRequest request,
        in VulkanAdvancedVisibilityLateTargetClosure closure,
        VkRenderProgram buildDepthPyramidProgram,
        VkRenderProgram lateVisibilityProgram)
    {
        if (!closure.IsValid || (uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
            return false;

        ref readonly FrameOperationHeader header = ref _headers[index];
        VulkanAdvancedVisibilityOperationPayload payload =
            _payloads.AdvancedVisibilities[header.PayloadIndex];
        if (!payload.Request.Equals(request))
            return false;

        Pipeline depthPipeline = buildDepthPyramidProgram.GetOrCreateComputePipeline();
        Pipeline latePipeline = lateVisibilityProgram.GetOrCreateComputePipeline();
        if (depthPipeline.Handle == 0 || latePipeline.Handle == 0 ||
            buildDepthPyramidProgram.PipelineLayout.Handle == 0 ||
            lateVisibilityProgram.PipelineLayout.Handle == 0)
        {
            return false;
        }

        _payloads.AdvancedVisibilities[header.PayloadIndex] = payload with
        {
            LateTargetClosure = closure,
            BuildDepthPyramidProgram = buildDepthPyramidProgram,
            BuildDepthPyramidPipeline = depthPipeline,
            BuildDepthPyramidLinkGeneration = buildDepthPyramidProgram.LinkGeneration,
            LateVisibilityProgram = lateVisibilityProgram,
            LateVisibilityPipeline = latePipeline,
            LateVisibilityLinkGeneration = lateVisibilityProgram.LinkGeneration,
        };
        return true;
    }

    internal bool TrySealAdvancedVisibilityLateDescriptors(
        int index,
        in VulkanAdvancedVisibilityStageRequest request,
        DescriptorSet[] descriptorSets,
        int descriptorSetCount)
    {
        if ((uint)index >= (uint)_count ||
            _headers[index].OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
            return false;

        ref readonly FrameOperationHeader header = ref _headers[index];
        VulkanAdvancedVisibilityOperationPayload payload =
            _payloads.AdvancedVisibilities[header.PayloadIndex];
        if (!payload.Request.Equals(request) || payload.LateTargetClosure is not { } closure ||
            descriptorSetCount != checked(
                (closure.DispatchCount + 1) * (int)closure.ViewCount) ||
            descriptorSets.Length < descriptorSetCount)
            return false;

        for (int setIndex = 0; setIndex < descriptorSetCount; ++setIndex)
            if (descriptorSets[setIndex].Handle == 0)
                return false;

        _payloads.AdvancedVisibilities[header.PayloadIndex] = payload with
        {
            LateTargetClosure = closure with
            {
                DescriptorSets = descriptorSets,
                DescriptorSetCount = descriptorSetCount,
            },
        };
        return true;
    }

    internal bool Contains(EVulkanPrimaryPlanNodeKind kind)
    {
        for (int index = 0; index < _count; index++)
            if (_headers[index].OpCode == kind)
                return true;
        return false;
    }

    /// <summary>
    /// Rebinds only the acquired-output UI image in admitted DLSS-G payloads.
    /// Headers, contexts, producer snapshots, and resource ordering remain
    /// frozen; this is the target-dependent counterpart to late WSI acquire.
    /// </summary>
    internal int BindAcquiredStreamlineUiImage(
        in VulkanStreamlineImage uiImage)
    {
        int rebound = 0;
        for (int index = 0; index < _count; index++)
        {
            ref readonly FrameOperationHeader header = ref _headers[index];
            if (header.OpCode !=
                EVulkanPrimaryPlanNodeKind.DlssFrameGeneration)
            {
                continue;
            }

            DlssFrameGenerationPayload payload =
                _payloads.DlssFrameGenerations[header.PayloadIndex];
            _payloads.DlssFrameGenerations[header.PayloadIndex] = payload with
            {
                UiColorAndAlpha = uiImage,
            };
            rebound++;
        }

        return rebound;
    }

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
            case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount: { var p=(MeshTaskDispatchIndirectCountOp)op; _payloads.MeshTasks[i]=new(p.Program,p.ProgramLinkGeneration,p.ProgramBindingSnapshot.CreateSealedCopy(),p.ProducerSnapshot,p.Pipeline,p.IndirectBuffer,p.CountBuffer,p.MaxDrawCount,p.Stride,p.ByteOffset,p.CountByteOffset,p.BindlessMaterialTextures); break; }
            case EVulkanPrimaryPlanNodeKind.ComputeDispatch: { var p=(ComputeDispatchOp)op; _payloads.ComputeDispatches[i]=new(p.Program,p.GroupsX,p.GroupsY,p.GroupsZ,p.Snapshot.CreateSealedCopy()); break; }
            case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect: { var p=(ComputeDispatchIndirectOp)op; _payloads.ComputeDispatchIndirects[i]=new(p.Program,p.Snapshot.CreateSealedCopy(),p.ArgumentOwner,p.ArgumentBuffer,p.ArgumentOffset,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.BufferCopy: { var p=(BufferCopyOp)op; _payloads.BufferCopies[i]=new(p.SourceOwner,p.SourceBuffer,p.SourceOffset,p.DestinationOwner,p.DestinationBuffer,p.DestinationOffset,p.ByteCount,p.RequireGpuWriteVisibility,p.DiagnosticReceipt,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.SubmissionMarker: { var p=(SubmissionMarkerOp)op; _payloads.SubmissionMarkers[i]=new(p.Fence,p.Label); break; }
            case EVulkanPrimaryPlanNodeKind.MemoryBarrier: _payloads.MemoryBarriers[i]=new(((MemoryBarrierOp)op).Mask); break;
            case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling: _payloads.PublishedFramebuffers[i]=new(((PublishFramebufferForSamplingOp)op).FrameBuffer); break;
            case EVulkanPrimaryPlanNodeKind.DlssUpscale: { var p=(DlssUpscaleOp)op; _payloads.DlssUpscales[i]=new(p.Session,p.SourceColor,p.Depth,p.Motion,p.OutputColor,p.Exposure,p.Parameters); break; }
            case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration: { var p=(DlssFrameGenerationOp)op; _payloads.DlssFrameGenerations[i]=new(p.Session,p.Depth,p.Motion,p.HudlessColor,p.Parameters,p.UiColorAndAlpha); break; }
            case EVulkanPrimaryPlanNodeKind.AdvancedVisibility: _payloads.AdvancedVisibilities[i] = new(((AdvancedVisibilityOp)op).Request, default, default, default, null, null, default, 0u, null, default, 0u, null, default, 0u, null, default, 0u); break;
            default: throw new InvalidOperationException($"No payload writer exists for {kind}.");
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_headers.Length >= required)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                _headers.Length,
                required);

        int capacity = Math.Max(required, _headers.Length * 2);
        Array.Resize(ref _headers, capacity);
        Array.Resize(ref _contexts, capacity);
        Array.Resize(ref _targets, capacity);
    }

    private void EnsureResourceUseCapacity(int required)
    {
        if (_resourceUses.Length >= required)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.ResourceUse,
                _resourceUses.Length,
                required);

        Array.Resize(
            ref _resourceUses,
            Math.Max(required, _resourceUses.Length * 2));
    }

    private void EnsureHeaderOrderCapacity(int required)
    {
        if (_headerOrderScratch.Length >= required)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                _headerOrderScratch.Length,
                required);

        Array.Resize(
            ref _headerOrderScratch,
            Math.Max(required, _headerOrderScratch.Length * 2));
    }
}
