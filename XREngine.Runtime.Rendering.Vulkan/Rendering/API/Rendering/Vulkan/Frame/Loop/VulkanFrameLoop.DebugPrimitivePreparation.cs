namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private readonly VulkanMeshRenderRequest[] _debugPrimitivePreparationRequests = new VulkanMeshRenderRequest[2];
    private readonly VulkanDebugIdlePreparationRecord[] _debugIdlePreparationRecords =
        new VulkanDebugIdlePreparationRecord[2];
    private int _nextDebugIdlePreparationRecord;
    private int _debugPrimitivePreparationBusy;

    internal bool CanPrepareDebugPrimitives
        => UseDynamicRenderingRenderTargets &&
           _commandRuntime.ResolveCurrentDrawTarget() is not null;

    /// <summary>
    /// Captures debug point and line requests in the active pass, prepares their
    /// pipelines, and releases the captured requests without publishing draws.
    /// </summary>
    internal EDebugPrimitivePreparationStatus TryPrepareDebugPrimitives(
        Action emitDraws, int expectedRequestCount, bool idle, out string reason)
    {
        if (!UseDynamicRenderingRenderTargets)
        {
            reason = "Debug pipeline preparation requires dynamic framebuffer rendering.";
            return EDebugPrimitivePreparationStatus.Unsupported;
        }
        if (System.Threading.Interlocked.Exchange(ref _debugPrimitivePreparationBusy, 1) != 0)
        {
            reason = "Debug pipeline preparation is in use by another render pass.";
            return EDebugPrimitivePreparationStatus.Pending;
        }

        ulong captureLeaseToken = 0UL;
        int requestCount = 0;
        reason = string.Empty;
        try
        {
            if (idle && TryReuseDebugIdlePreparation(emitDraws))
            {
                reason = "Ready";
                return EDebugPrimitivePreparationStatus.Ready;
            }

            requestCount = MeshOperationRequests.CaptureTo(
                emitDraws,
                _debugPrimitivePreparationRequests,
                out captureLeaseToken,
                out _);
            if (requestCount < 0 || requestCount != expectedRequestCount)
            {
                reason = "Debug draw request capture produced an unexpected number of requests.";
                return EDebugPrimitivePreparationStatus.Failed;
            }

            VulkanCommandThreadContext threadContext = _commandRuntime.ThreadWorkspace.Current;
            ResourcePlannerRuntimeGeneration plannerGeneration =
                ReferenceEquals(threadContext.ResourcePlannerRuntimeStateOwner, _commandRuntime) &&
                threadContext.ResourcePlannerRuntimeGeneration is { } scopedGeneration
                    ? scopedGeneration
                    : _framePlanner.GetPublishedResourcePlannerGeneration();

            EDebugPrimitivePreparationStatus result = EDebugPrimitivePreparationStatus.Ready;
            string firstDetail = string.Empty;
            for (int index = 0; index < requestCount; index++)
            {
                ref VulkanMeshRenderRequest request = ref _debugPrimitivePreparationRequests[index];
                ref readonly FrameOpContext context =
                    ref VulkanMeshRenderRequest.GetContextReference(in request);
                ref readonly VulkanMeshProducerSnapshot producer =
                    ref VulkanMeshRenderRequest.GetProducerReference(in request);
                if (producer.Target is null)
                {
                    result = EDebugPrimitivePreparationStatus.Unsupported;
                    firstDetail = "Debug pipeline preparation requires the active framebuffer target.";
                    break;
                }

                if (!_resourcePlannerSessions.TryEnterQueuedMeshPlannerGeneration(
                        plannerGeneration,
                        in context,
                        out VulkanPreparedResourcePlannerThreadScope plannerScope,
                        out string plannerReason))
                {
                    if (result != EDebugPrimitivePreparationStatus.Failed)
                        result = EDebugPrimitivePreparationStatus.Pending;
                    if (firstDetail.Length == 0)
                        firstDetail = plannerReason;
                    continue;
                }

                using (plannerScope)
                {
                    ResourcePlannerRuntimeGeneration activeGeneration =
                        threadContext.ResourcePlannerRuntimeGeneration!;
                    int descriptorViewFamilyIdentity = context.OutputTargetIdentity != 0
                        ? context.OutputTargetIdentity
                        : context.ViewportIdentity;
                    VulkanMeshMaterializationSnapshot snapshot = new(
                        activeGeneration,
                        context,
                        descriptorViewFamilyIdentity,
                        false,
                        false,
                        _telemetry);
                    if (!request.Renderer.TryMaterializeQueuedRenderRequest(
                            in request,
                            in producer,
                            in snapshot,
                            prewarmDescriptorAllocation: false,
                            out VulkanMeshOperationRequest operation))
                    {
                        if (request.Renderer.HasTerminalProgramPreparationFailure)
                            result = EDebugPrimitivePreparationStatus.Failed;
                        else if (result != EDebugPrimitivePreparationStatus.Failed)
                            result = EDebugPrimitivePreparationStatus.Pending;
                        if (firstDetail.Length == 0)
                            firstDetail = request.Renderer.LastPrepareDetail;
                        continue;
                    }

                    try
                    {
                        EDebugPrimitivePreparationStatus status =
                            _commandRuntime.TryPrewarmDebugPrimitivePipeline(
                                in operation,
                                activeGeneration,
                                UseDynamicRenderingRenderTargets,
                                out string pipelineReason);
                        if (status != EDebugPrimitivePreparationStatus.Ready)
                        {
                            if (status == EDebugPrimitivePreparationStatus.Failed ||
                                result == EDebugPrimitivePreparationStatus.Ready)
                                result = status;
                            if (firstDetail.Length == 0)
                                firstDetail = pipelineReason;
                        }
                    }
                    finally
                    {
                        operation.Draw.ProgramBindingSnapshot?.ReleaseIfUnpooled();
                    }
                }
            }

            reason = result == EDebugPrimitivePreparationStatus.Ready ? "Ready" : firstDetail;
            if (idle && result == EDebugPrimitivePreparationStatus.Ready)
                RecordDebugIdlePreparation(emitDraws);
            return result;
        }
        catch (RenderResourcePreparationPendingException exception)
        {
            reason = exception.Message;
            return EDebugPrimitivePreparationStatus.Pending;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return EDebugPrimitivePreparationStatus.Failed;
        }
        finally
        {
            try
            {
                for (int index = 0; index < _debugPrimitivePreparationRequests.Length; index++)
                    VulkanMeshRenderRequest.ReleaseAuthoringLease(
                        ref _debugPrimitivePreparationRequests[index]);
            }
            finally
            {
                _debugPrimitivePreparationRequests.AsSpan().Clear();
                try
                {
                    if (captureLeaseToken != 0UL)
                        MeshOperationRequests.ReleaseCapturePublicationLeases(captureLeaseToken);
                }
                finally
                {
                    System.Threading.Volatile.Write(ref _debugPrimitivePreparationBusy, 0);
                }
            }
        }
    }

    private bool TryReuseDebugIdlePreparation(Action emitDraws)
    {
        XRRenderPipelineInstance? pipeline =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null)
            return false;
        XRFrameBuffer? target = _commandRuntime.ResolveCurrentDrawTarget();
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        for (int index = 0; index < _debugIdlePreparationRecords.Length; index++)
        {
            ref readonly VulkanDebugIdlePreparationRecord entry =
                ref _debugIdlePreparationRecords[index];
            if (ReferenceEquals(entry.Emitter, emitDraws) &&
                ReferenceEquals(entry.Pipeline, pipeline) &&
                ReferenceEquals(entry.Viewport, pipeline.RenderState.WindowViewport) &&
                entry.ResourceGeneration == pipeline.ResourceGeneration &&
                ReferenceEquals(entry.Target, target) &&
                entry.PassIndex == passIndex)
                return true;
        }

        return false;
    }

    private void RecordDebugIdlePreparation(Action emitDraws)
    {
        XRRenderPipelineInstance? pipeline =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null)
            return;
        int slot = _nextDebugIdlePreparationRecord;
        _nextDebugIdlePreparationRecord = slot + 1 == _debugIdlePreparationRecords.Length
            ? 0
            : slot + 1;
        ref VulkanDebugIdlePreparationRecord entry = ref _debugIdlePreparationRecords[slot];
        entry.Emitter = emitDraws;
        entry.Pipeline = pipeline;
        entry.Viewport = pipeline.RenderState.WindowViewport;
        entry.ResourceGeneration = pipeline.ResourceGeneration;
        entry.Target = _commandRuntime.ResolveCurrentDrawTarget();
        entry.PassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
    }
}
